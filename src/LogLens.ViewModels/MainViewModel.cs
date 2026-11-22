using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Loading;

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
/// The window's state: the open log (v0.1 shows one at a time; tabs arrive in v0.4), the load in progress and the current notice.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string AppName = "LogLens";

    private readonly ILogDocumentLoader _loader;
    private CancellationTokenSource? _loadCancellation;

    public MainViewModel(ILogDocumentLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog), nameof(WindowTitle), nameof(ShowEmptyState))]
    public partial LogViewModel? CurrentLog { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string LoadingText { get; private set; } = string.Empty;

    /// <summary>Fraction of the file read so far (0 … 1).</summary>
    [ObservableProperty]
    public partial double LoadProgress { get; private set; }

    /// <summary>The message bar's content; null when there is nothing to say.</summary>
    [ObservableProperty]
    public partial Notice? Notice { get; set; }

    public bool HasLog => CurrentLog is not null;

    /// <summary>UX-2: the drop zone shows while nothing is open or loading.</summary>
    public bool ShowEmptyState => CurrentLog is null && !IsLoading;

    public string WindowTitle => CurrentLog is { } log ? $"{log.Document.FileName} — {AppName}" : AppName;

    /// <summary>
    /// Opens the first of <paramref name="paths"/>, replacing the current log once it has loaded. Opening several files at once arrives with
    /// tabs (FR-O2, v0.4); until then the others are named in a notice. Failures become a notice, never an exception.
    /// </summary>
    public async Task OpenAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        var path = paths[0];
        var others = paths.Count - 1;

        _loadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        Notice = null;
        IsLoading = true;
        LoadProgress = 0;
        LoadingText = $"Opening {Path.GetFileName(path)}…";
        var progress = new Progress<double>(p =>
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                LoadProgress = p;
            }
        });

        try
        {
            var document = await _loader.LoadAsync(path, progress, cancellation.Token);
            if (!ReferenceEquals(_loadCancellation, cancellation))
            {
                return; // a newer open replaced this one
            }

            CurrentLog = new LogViewModel(document);
            Notice = NoticeFor(document, others);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // cancelled by the user or replaced by a newer open: keep whatever is shown
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                Notice = ErrorNotice(path, ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                IsLoading = false;
                LoadingText = string.Empty;
            }
        }
    }

    /// <summary>Cancels the load in progress (UX-4); the previously open log stays.</summary>
    public void CancelLoad() => _loadCancellation?.Cancel();

    /// <summary>Closes the open log and shows the empty state again.</summary>
    public void Close()
    {
        CancelLoad();
        CurrentLog = null;
        Notice = null;
    }

    /// <summary>Something went wrong that the view could not handle; shown instead of crashing.</summary>
    public void ReportUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Notice = new Notice(NoticeSeverity.Error, "Something went wrong", exception.Message);
    }

    private static Notice? NoticeFor(Core.Models.LogDocument document, int othersSkipped)
    {
        // UX-3: "0 events parsed — check the format".
        if (document.Events.Count == 0)
        {
            return new Notice(NoticeSeverity.Informational, "The file is empty", $"{document.FileName} has no log events yet.");
        }

        if (document.UnparsedCount == document.Events.Count)
        {
            return new Notice(
                NoticeSeverity.Warning,
                "0 events parsed — check the format",
                $"No line of {document.FileName} matched {document.Format.DisplayName} (the default Serilog template). The lines are shown as they are.");
        }

        return othersSkipped > 0
            ? new Notice(
                NoticeSeverity.Informational,
                "One file at a time",
                $"Opened {document.FileName}. This version shows one log at a time, so {othersSkipped} other {(othersSkipped == 1 ? "file was" : "files were")} not opened.")
            : null;
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
