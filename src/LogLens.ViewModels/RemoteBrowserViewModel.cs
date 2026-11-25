using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Remote;

namespace LogLens.ViewModels;

/// <summary>One part of the folder path shown above the listing: its name and the folder it opens.</summary>
public sealed record RemoteCrumb(string Name, string Path);

/// <summary>The sub-folders of a folder, by name, or why they couldn't be listed.</summary>
public sealed record RemoteSubFolders(IReadOnlyList<RemoteEntry> Folders, string? Error);

/// <summary>What the files in the listing are ordered by.</summary>
public enum RemoteSort
{
    Name,
    Modified,
}

/// <summary>
/// Browsing one remote host's folders (FR-O7): the current folder, its sub-folders and the log files in it that match the pattern
/// and the filter text, ordered by name or by last modified time.
/// Listing runs on a worker thread; starting another listing cancels the one still running, so only the latest folder is ever shown.
/// Which files are ticked is left to the view (the list control keeps the selection).
/// </summary>
public sealed partial class RemoteBrowserViewModel : ObservableObject
{
    private readonly IRemoteFolders _folders;
    private IReadOnlyList<RemoteEntry> _all = [];
    private int _matchedByPattern;
    private CancellationTokenSource? _listing;
    private string _patterns;
    private string _filter = string.Empty;
    private RemoteSort _sortBy = RemoteSort.Modified; // the latest files first
    private bool _sortDescending = true;

    public RemoteBrowserViewModel(IRemoteFolders folders, string? patterns = null)
    {
        _folders = folders;
        _patterns = string.IsNullOrWhiteSpace(patterns) ? FilePatterns.Default : patterns.Trim();
        Folder = folders.StartFolder;
    }

    public string HostName => _folders.DisplayName;

    /// <summary>The folder being shown.</summary>
    [ObservableProperty]
    public partial string Folder { get; private set; }

    /// <summary>The path of <see cref="Folder"/> from the top down (<c>/</c>, <c>var</c>, <c>log</c>), each part opening its folder.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RemoteCrumb> Crumbs { get; private set; } = [];

    /// <summary>Sub-folders first, then the matching files, each by name (date order for rolling logs).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RemoteEntry> Entries { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Why the folder couldn't be listed, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The file name patterns (<c>*.txt;*.log</c>); changing them filters the listing again without reading the folder again.</summary>
    public string Patterns
    {
        get => _patterns;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? FilePatterns.Default : value.Trim();
            if (SetProperty(ref _patterns, next))
            {
                Rearrange();
            }
        }
    }

    /// <summary>Text the file names must contain (case-insensitive); applies on top of <see cref="Patterns"/>. Folders are not filtered.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value?.Trim() ?? string.Empty))
            {
                Rearrange();
            }
        }
    }

    public RemoteSort SortBy => _sortBy;

    /// <summary>True for Z–A, or for newest first when sorting by <see cref="RemoteSort.Modified"/>.</summary>
    public bool SortDescending => _sortDescending;

    /// <summary>Orders the listing again (without reading the folder again): files by <paramref name="by"/>; folders by name.</summary>
    public void Sort(RemoteSort by, bool descending)
    {
        if (_sortBy == by && _sortDescending == descending)
        {
            return;
        }

        _sortBy = by;
        _sortDescending = descending;
        OnPropertyChanged(nameof(SortBy));
        OnPropertyChanged(nameof(SortDescending));
        Rearrange();
    }

    private void Rearrange()
    {
        var list = FilePatterns.Split(_patterns);
        _matchedByPattern = _all.Count(e => !e.IsFolder && FilePatterns.MatchesAny(list, e.Name));
        Entries = Arrange(_all, _patterns, _filter, _sortBy, _sortDescending);
        OnPropertyChanged(nameof(Summary));
    }

    public bool CanGoUp => _folders.Parent(Folder) is not null;

    /// <summary>"3 folders, 12 matching files (40 other files hidden by the pattern, 2 files hidden by the filter)".</summary>
    public string Summary
    {
        get
        {
            if (IsBusy)
            {
                return "Reading the folder…";
            }

            if (Error is not null)
            {
                return string.Empty;
            }

            var folders = Entries.Count(e => e.IsFolder);
            var files = Entries.Count - folders;
            var byPattern = _all.Count(e => !e.IsFolder) - _matchedByPattern;
            var byFilter = _matchedByPattern - files;
            var text = string.Create(CultureInfo.InvariantCulture, $"{Plural(folders, "folder")}, {Plural(files, "matching file")}");
            var notes = new List<string>(2);
            if (byPattern > 0)
            {
                notes.Add($"{Plural(byPattern, "other file")} hidden by the pattern");
            }

            if (byFilter > 0)
            {
                notes.Add($"{Plural(byFilter, "file")} hidden by the filter");
            }

            return notes.Count > 0 ? $"{text} ({string.Join(", ", notes)})" : text;
        }
    }

    private static string Plural(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");

    /// <summary>Lists the start folder.</summary>
    public Task LoadAsync() => OpenFolderAsync(_folders.StartFolder);

    public Task UpAsync() => _folders.Parent(Folder) is { } parent ? OpenFolderAsync(parent) : Task.CompletedTask;

    public Task RefreshAsync() => OpenFolderAsync(Folder);

    /// <summary>
    /// The sub-folders of <paramref name="folder"/>, for the list behind a part of the path; the shown folder's come from its listing.
    /// Doesn't change what is shown.
    /// </summary>
    public async Task<RemoteSubFolders> ListSubFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        if (folder == Folder && !IsBusy && Error is null)
        {
            return new RemoteSubFolders(SortedFolders(_all), null);
        }

        try
        {
            return new RemoteSubFolders(SortedFolders(await _folders.ListAsync(folder, cancellationToken)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new RemoteSubFolders([], Describe(ex));
        }
    }

    private static List<RemoteEntry> SortedFolders(IEnumerable<RemoteEntry> entries)
        => [.. entries.Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    partial void OnFolderChanged(string value) => Crumbs = BuildCrumbs(_folders, value);

    /// <summary>
    /// <paramref name="folder"/> and the folders above it, top first. The top keeps its whole path (<c>/</c>, <c>\\server\share</c>);
    /// the others show their own name.
    /// </summary>
    public static IReadOnlyList<RemoteCrumb> BuildCrumbs(IRemoteFolders folders, string folder)
    {
        var crumbs = new List<RemoteCrumb>();
        for (var path = folder; crumbs.Count < 256;)
        {
            var parent = folders.Parent(path);
            if (parent is null || parent == path)
            {
                crumbs.Add(new RemoteCrumb(path, path));
                break;
            }

            crumbs.Add(new RemoteCrumb(LastPart(path), path));
            path = parent;
        }

        crumbs.Reverse();
        return crumbs;
    }

    private static string LastPart(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path : trimmed[(cut + 1)..];
    }

    /// <summary>Shows <paramref name="folder"/>. If it can't be read, the list is empty and <see cref="Error"/> says why.</summary>
    public async Task OpenFolderAsync(string folder)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? Folder : folder.Trim();
        _listing?.Cancel();
        var cts = _listing = new CancellationTokenSource();

        Folder = folder;
        Error = null;
        IsBusy = true;
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(Summary));

        try
        {
            var entries = await _folders.ListAsync(folder, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _all = entries;
            Rearrange();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return; // a newer listing took over
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _all = [];
            _matchedByPattern = 0;
            Entries = [];
            Error = Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_listing, cts))
            {
                IsBusy = false;
                OnPropertyChanged(nameof(Summary));
                _listing = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Folders by name, then the files that match one of <paramref name="patterns"/> and contain <paramref name="filter"/>,
    /// by name or by last modified time (equal times by name).
    /// </summary>
    public static IReadOnlyList<RemoteEntry> Arrange(
        IEnumerable<RemoteEntry> entries,
        string patterns,
        string? filter = null,
        RemoteSort sortBy = RemoteSort.Name,
        bool descending = false)
    {
        var list = FilePatterns.Split(patterns);
        var text = filter?.Trim() ?? string.Empty;
        var folders = entries.Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var files = entries
            .Where(e => !e.IsFolder && FilePatterns.MatchesAny(list, e.Name) && e.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

        IOrderedEnumerable<RemoteEntry> ordered = (sortBy, descending) switch
        {
            (RemoteSort.Modified, false) => files.OrderBy(e => e.LastWriteTime).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            (RemoteSort.Modified, true) => files.OrderByDescending(e => e.LastWriteTime).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            (_, true) => files.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

        return [.. folders, .. ordered];
    }

    private static string Describe(Exception ex) => ex switch
    {
        DirectoryNotFoundException => "This folder doesn't exist.",
        UnauthorizedAccessException => "You don't have permission to read this folder.",
        OperationCanceledException => "Listing the folder took too long.",
        _ => ex.Message,
    };
}
