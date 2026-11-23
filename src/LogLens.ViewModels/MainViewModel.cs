using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Models;
using LogLens.Core.State;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels;

/// <summary>How serious a <see cref="Notice"/> is; the view maps it to an <c>InfoBar</c> severity.</summary>
public enum NoticeSeverity
{
    Informational,
    Warning,
    Error,
}

/// <summary>A non-blocking message for the user (UX-3, UX-7).</summary>
public sealed record Notice(NoticeSeverity Severity, string Title, string Message);

/// <summary>
/// The window's state: the open tabs and the one in front (FR-O2), files and merged views being opened (FR-O4), the current notice, the recent
/// files (FR-O1) and the session reopened on the next start (FR-P3).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string AppName = "LogLens";

    /// <summary>More files than this in one go open only the first ones: a folder of thousands of files is not a set of tabs.</summary>
    public const int MaxTabsPerOpen = 20;

    private readonly ILogStreamFactory _streams;
    private readonly RecentFilesService _recent;
    private readonly SessionService _session;
    private readonly BookmarkService _bookmarks;
    private readonly Dictionary<LogViewModel, string> _pending = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _restoring;

    public MainViewModel(ILogStreamFactory streams, RecentFilesService recent, SessionService session, BookmarkService bookmarks)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _recent = recent ?? throw new ArgumentNullException(nameof(recent));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));
        _recent.Changed += (_, _) => OnPropertyChanged(nameof(RecentFiles));
    }

    /// <summary>The open logs, one per tab, in tab order.</summary>
    public ObservableCollection<LogViewModel> Tabs { get; } = [];

    /// <summary>The tab in front; null when nothing is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog), nameof(WindowTitle), nameof(ShowEmptyState))]
    public partial LogViewModel? CurrentLog { get; set; }

    /// <summary>A file is being opened and has not shown anything yet; the tabs that are open stay as they are meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string LoadingText { get; private set; } = string.Empty;

    /// <summary>The message bar's content; null when there is nothing to say.</summary>
    [ObservableProperty]
    public partial Notice? Notice { get; set; }

    /// <summary>Whether the open logs are remembered for the next start (false for a second, independent window).</summary>
    public bool RemembersSession { get; set; } = true;

    public bool HasLog => CurrentLog is not null;

    /// <summary>UX-2: the drop zone shows while nothing is open or opening.</summary>
    public bool ShowEmptyState => CurrentLog is null && !IsLoading;

    public string WindowTitle => CurrentLog is { } log ? $"{log.Document.FileName} — {AppName}" : AppName;

    /// <summary>Recently opened files, newest first (FR-O1).</summary>
    public IReadOnlyList<string> RecentFiles => _recent.Files;

    /// <summary>The name a tab shows for <paramref name="log"/>.</summary>
    public static string TabTitle(LogViewModel log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Document.FileName;
    }

    /// <summary>The tooltip of <paramref name="log"/>'s tab: its file, or every file of a merged view.</summary>
    public static string TabToolTip(LogViewModel log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Document.MergedFiles is { } merged ? string.Join('\n', merged) : log.Document.CurrentFilePath ?? log.Document.FileName;
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Opens each of <paramref name="paths"/> in a tab of its own (FR-O2), one after another, and brings the last one to the front; a file that is
    /// already open just comes to the front. A tab appears once its file has something to show (it may still be loading), so a file that can't
    /// be read adds no tab. Failures become a notice, never an exception. Only the first <see cref="MaxTabsPerOpen"/> files are opened.
    /// </summary>
    public async Task OpenAsync(IReadOnlyList<string> paths, bool follow = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            return;
        }

        Notice = null;
        var skipped = Math.Max(0, files.Count - MaxTabsPerOpen);
        var cancellation = LoadCancellation();
        foreach (var path in files.Take(MaxTabsPerOpen))
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (FindTab(path) is { } open)
            {
                CurrentLog = open;
                continue;
            }

            await OpenTabAsync(path, mergedFiles: null, follow, cancellation.Token);
        }

        if (skipped > 0)
        {
            Notice = new Notice(
                NoticeSeverity.Informational,
                $"Opened the first {MaxTabsPerOpen} files",
                $"{skipped} more {(skipped == 1 ? "was" : "were")} not opened. Merge them into one view to see them all.");
        }
    }

    /// <summary>
    /// Opens <paramref name="paths"/> as one merged view (FR-O4): their events in time order in one tab, with the file of each event. It is a
    /// snapshot and does not follow the files. One file opens on its own; the same set of files already merged just comes to the front.
    /// </summary>
    public async Task OpenMergedAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count < 2)
        {
            await OpenAsync(files);
            return;
        }

        if (FindMergedTab(files) is { } open)
        {
            CurrentLog = open;
            return;
        }

        Notice = null;
        await OpenTabAsync(path: null, files, follow: false, LoadCancellation().Token);
    }

    /// <summary>Reopens the tabs that were open when LogLens last ran, in their order, with the same one in front (FR-P3).</summary>
    public async Task RestoreSessionAsync()
    {
        var session = _session.Current;
        if (session.Tabs.Count == 0)
        {
            return;
        }

        var cancellation = LoadCancellation();
        LogViewModel? selected = null;
        _restoring = true; // what is half restored is not saved over the full session
        try
        {
            for (var i = 0; i < session.Tabs.Count && !cancellation.IsCancellationRequested; i++)
            {
                var tab = session.Tabs[i];
                var log = tab.MergedFiles is { } merged
                    ? FindMergedTab(merged) ?? await OpenTabAsync(path: null, merged, follow: false, cancellation.Token)
                    : FindTab(tab.Path!) ?? await OpenTabAsync(tab.Path, mergedFiles: null, tab.Follow, cancellation.Token);
                if (i == session.Selected && log is not null)
                {
                    selected = log;
                }
            }
        }
        finally
        {
            _restoring = false;
        }

        if (selected is not null && Tabs.Contains(selected))
        {
            CurrentLog = selected;
        }

        SaveSession();
    }

    /// <summary>
    /// Opens one tab: a file (<paramref name="path"/>) or a merged view (<paramref name="mergedFiles"/>). Completes with the new log once it shows,
    /// or with null when it failed or was cancelled before that.
    /// </summary>
    private Task<LogViewModel?> OpenTabAsync(string? path, IReadOnlyList<string>? mergedFiles, bool follow, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return Task.FromResult<LogViewModel?>(null);
        }

        ILogStream stream;
        try
        {
            stream = mergedFiles is null ? _streams.Open(path!) : _streams.OpenMerged(mergedFiles);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            Notice = ErrorNotice(path ?? mergedFiles![0], ex);
            return Task.FromResult<LogViewModel?>(null);
        }

        var log = new LogViewModel(new LogDocument(path, stream.Format), _bookmarks) { IsFollowing = follow && mergedFiles is null };
        var shown = new TaskCompletionSource<LogViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginLoading(log, mergedFiles is null
            ? $"Opening {Path.GetFileName(path)}…"
            : $"Merging {mergedFiles.Count} files…");

        log.ContentArrived += OnContentArrived;
        log.StreamFailed += OnStreamFailed;
        log.LoadCompleted += (_, _) =>
        {
            if (NoticeFor(log.Document) is { } notice)
            {
                ShowNotice(log, notice);
            }
        };
        log.NoticeRaised += (_, notice) => ShowNotice(log, notice);
        log.FileSwitched += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowTitle));
            TabChanged?.Invoke(this, log);
        };
        log.PropertyChanged += OnLogPropertyChanged;

        var run = log.RunAsync(stream, token);
        _ = WhenStoppedAsync();
        return shown.Task;

        async Task WhenStoppedAsync()
        {
            await run;
            if (!Tabs.Contains(log))
            {
                EndLoading(log);
                log.Dispose(); // cancelled or failed before it showed anything
            }

            shown.TrySetResult(null);
        }

        void OnContentArrived(object? sender, EventArgs e)
        {
            log.ContentArrived -= OnContentArrived;
            EndLoading(log);
            if (token.IsCancellationRequested)
            {
                log.Dispose(); // cancelled
                shown.TrySetResult(null);
                return;
            }

            Tabs.Add(log);
            CurrentLog = log;
            if (path is not null)
            {
                _recent.Add(path);
            }

            SaveSession();
            shown.TrySetResult(log);
        }

        void OnStreamFailed(object? sender, Exception ex)
        {
            if (Tabs.Contains(log))
            {
                return; // reported by the log itself ("Stopped reading …")
            }

            EndLoading(log);
            if (!token.IsCancellationRequested)
            {
                var (failedPath, reason) = ex is MergeSourceException merge ? (merge.FilePath, merge.InnerException ?? merge) : (path ?? mergedFiles![0], ex);
                Notice = ErrorNotice(failedPath, reason);
                if (path is not null && reason is FileNotFoundException or DirectoryNotFoundException)
                {
                    _recent.Remove(path); // a recent file that is gone is not offered again
                }
            }

            log.Dispose();
            shown.TrySetResult(null);
        }
    }

    /// <summary>Raised when what a tab shows in its header changed (a rolling log moved on to its next file).</summary>
    public event EventHandler<LogViewModel>? TabChanged;

    // ------------------------------------------------------------------ tabs

    /// <summary>The tab showing <paramref name="path"/> on its own, if any.</summary>
    public LogViewModel? FindTab(string path)
        => Tabs.FirstOrDefault(t => !t.Document.IsMerged && string.Equals(t.Document.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private LogViewModel? FindMergedTab(IReadOnlyList<string> files)
        => Tabs.FirstOrDefault(t => t.Document.MergedFiles is { } merged
                                    && merged.Count == files.Count
                                    && merged.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(files.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase));

    /// <summary>Closes <paramref name="log"/>'s tab and stops reading its file; the tab next to it comes to the front.</summary>
    public void CloseTab(LogViewModel log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var index = Tabs.IndexOf(log);
        if (index < 0)
        {
            return;
        }

        log.PropertyChanged -= OnLogPropertyChanged;
        Tabs.RemoveAt(index);
        if (ReferenceEquals(CurrentLog, log))
        {
            CurrentLog = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        log.Dispose();
        if (Tabs.Count == 0)
        {
            Notice = null;
        }

        SaveSession();
    }

    /// <summary>Closes the tab in front (Ctrl+W).</summary>
    public void Close()
    {
        if (CurrentLog is { } log)
        {
            CloseTab(log);
        }
    }

    /// <summary>Closes every tab but <paramref name="keep"/>.</summary>
    public void CloseOthers(LogViewModel keep)
    {
        foreach (var log in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            CloseTab(log);
        }
    }

    /// <summary>A tab was dragged to another place in the strip.</summary>
    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count || from == to)
        {
            return;
        }

        Tabs.Move(from, to);
        SaveSession();
    }

    /// <summary>Brings the next (or previous) tab to the front, wrapping around (Ctrl+Tab / Ctrl+Shift+Tab).</summary>
    public void SelectNextTab(bool forward)
    {
        if (Tabs.Count < 2 || CurrentLog is null)
        {
            return;
        }

        var index = Tabs.IndexOf(CurrentLog);
        CurrentLog = Tabs[(index + (forward ? 1 : Tabs.Count - 1)) % Tabs.Count];
    }

    partial void OnCurrentLogChanged(LogViewModel? value) => SaveSession();

    // ------------------------------------------------------------------ loading

    /// <summary>Forgets the recent files list.</summary>
    public void ClearRecentFiles() => _recent.Clear();

    /// <summary>Cancels every open in progress (UX-4); the open tabs stay.</summary>
    public void CancelLoad()
    {
        if (_loadCancellation is { } cancellation)
        {
            _loadCancellation = null;
            cancellation.Cancel();
        }

        _pending.Clear();
        SyncLoading();
    }

    private CancellationTokenSource LoadCancellation() => _loadCancellation ??= new CancellationTokenSource();

    private void BeginLoading(LogViewModel log, string text)
    {
        _pending[log] = text;
        SyncLoading();
    }

    private void EndLoading(LogViewModel log)
    {
        if (_pending.Remove(log))
        {
            SyncLoading();
        }
    }

    private void SyncLoading()
    {
        IsLoading = _pending.Count > 0;
        LoadingText = _pending.Count switch
        {
            0 => string.Empty,
            1 => _pending.Values.First(),
            var n => $"Opening {n} logs…",
        };
    }

    // ------------------------------------------------------------------ notices

    /// <summary>Something went wrong that the view could not handle; shown instead of crashing.</summary>
    public void ReportUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Notice = new Notice(NoticeSeverity.Error, "Something went wrong", exception.Message);
    }

    /// <summary>A notice from a tab; one from a tab that is not in front says which tab it is about.</summary>
    private void ShowNotice(LogViewModel log, Notice notice)
        => Notice = ReferenceEquals(log, CurrentLog) || !Tabs.Contains(log) ? notice : notice with { Title = $"{TabTitle(log)}: {notice.Title}" };

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.IsFollowing))
        {
            SaveSession();
        }
    }

    private void SaveSession()
    {
        if (!RemembersSession || _restoring)
        {
            return;
        }

        var tabs = Tabs.Select(t => t.Document.MergedFiles is { } merged
            ? new SessionTab { MergedFiles = merged }
            : new SessionTab { Path = t.Document.FilePath, Follow = t.IsFollowing }).ToList();
        _session.Save(new SessionState { Tabs = tabs, Selected = CurrentLog is null ? 0 : Math.Max(0, Tabs.IndexOf(CurrentLog)) });
    }

    private static bool IsReadError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static Notice? NoticeFor(LogDocument document)
    {
        // UX-3: "0 events parsed — check the format".
        if (document.Events.Count == 0)
        {
            return document.IsMerged
                ? new Notice(NoticeSeverity.Informational, "The files are empty", "None of the merged files has log events yet.")
                : new Notice(NoticeSeverity.Informational, "The file is empty", $"{document.FileName} has no log events yet.");
        }

        if (document.UnparsedCount == document.Events.Count)
        {
            return new Notice(
                NoticeSeverity.Warning,
                "0 events parsed — check the format",
                $"No line of {document.FileName} matched {document.Format.DisplayName} (the default Serilog template). The lines are shown as they are.");
        }

        return null;
    }

    internal static Notice ErrorNotice(string path, Exception ex)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException
                => new Notice(NoticeSeverity.Error, "File not found", $"{path} does not exist. It may have been moved or deleted."),
            UnauthorizedAccessException or System.Security.SecurityException
                => new Notice(NoticeSeverity.Error, "Access denied", $"You don't have permission to read {name}."),
            _ => new Notice(NoticeSeverity.Error, $"Could not open {name}", ex.Message),
        };
    }
}
