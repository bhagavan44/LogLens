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
/// The window's state: the open log (one at a time until tabs arrive in v0.4), the open in progress, the current notice, the recent files
/// (FR-O1) and the session reopened on the next start (FR-P3).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string AppName = "LogLens";

    private readonly ILogStreamFactory _streams;
    private readonly RecentFilesService _recent;
    private readonly SessionService _session;
    private CancellationTokenSource? _openCancellation;

    public MainViewModel(ILogStreamFactory streams, RecentFilesService recent, SessionService session)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _recent = recent ?? throw new ArgumentNullException(nameof(recent));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _recent.Changed += (_, _) => OnPropertyChanged(nameof(RecentFiles));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog), nameof(WindowTitle), nameof(ShowEmptyState))]
    public partial LogViewModel? CurrentLog { get; private set; }

    /// <summary>A file is being opened and has not shown anything yet; the current log stays until it has.</summary>
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


    /// <summary>
    /// Opens the first of <paramref name="paths"/>. The current log stays until the new one has something to show, so a file that can't be
    /// read leaves it in place. Opening several files at once arrives with tabs (FR-O2, v0.4); until then the others are named in a notice.
    /// Completes once the log shows (it may still be loading) or the open failed; failures become a notice, never an exception.
    /// </summary>
    public Task OpenAsync(IReadOnlyList<string> paths, bool follow = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return Task.CompletedTask;
        }

        var path = paths[0];
        var others = paths.Count - 1;

        _openCancellation?.Cancel();
        var cancellation = _openCancellation = new CancellationTokenSource();

        ILogStream stream;
        try
        {
            stream = _streams.Open(path);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            _openCancellation = null;
            Notice = ErrorNotice(path, ex);
            return Task.CompletedTask;
        }

        Notice = null;
        IsLoading = true;
        LoadingText = $"Opening {Path.GetFileName(path)}…";

        var log = new LogViewModel(new LogDocument(path, stream.Format)) { IsFollowing = follow };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        log.ContentArrived += OnContentArrived;
        log.StreamFailed += OnStreamFailed;
        log.LoadCompleted += (_, _) =>
        {
            if (ReferenceEquals(CurrentLog, log) && NoticeFor(log.Document, others) is { } notice)
            {
                Notice = notice;
            }
        };
        log.NoticeRaised += (_, notice) =>
        {
            if (ReferenceEquals(CurrentLog, log))
            {
                Notice = notice;
            }
        };
        log.FileSwitched += (_, _) =>
        {
            if (ReferenceEquals(CurrentLog, log))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        };
        log.PropertyChanged += OnLogPropertyChanged;

        var run = log.RunAsync(stream, cancellation.Token);
        _ = WhenStoppedAsync();
        return shown.Task;

        async Task WhenStoppedAsync()
        {
            await run;
            if (!ReferenceEquals(CurrentLog, log))
            {
                log.Dispose(); // cancelled or failed before it showed anything
            }

            shown.TrySetResult();
        }

        void OnContentArrived(object? sender, EventArgs e)
        {
            log.ContentArrived -= OnContentArrived;
            if (!ReferenceEquals(_openCancellation, cancellation))
            {
                log.Dispose(); // cancelled, or replaced by a newer open
                shown.TrySetResult();
                return;
            }

            _openCancellation = null;
            var previous = CurrentLog;
            CurrentLog = log;
            previous?.Dispose();
            IsLoading = false;
            LoadingText = string.Empty;
            _recent.Add(path);
            SaveSession();
            shown.TrySetResult();
        }

        void OnStreamFailed(object? sender, Exception ex)
        {
            if (ReferenceEquals(CurrentLog, log))
            {
                return; // reported by the log itself ("Stopped reading …")
            }

            if (ReferenceEquals(_openCancellation, cancellation))
            {
                _openCancellation = null;
                IsLoading = false;
                LoadingText = string.Empty;
                Notice = ErrorNotice(path, ex);
                if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    _recent.Remove(path); // a recent file that is gone is not offered again
                }
            }

            log.Dispose();
            shown.TrySetResult();
        }
    }

    /// <summary>Reopens what was open when LogLens last ran (FR-P3). Does nothing when the session is empty.</summary>
    public Task RestoreSessionAsync()
    {
        var session = _session.Current;
        if (session.Tabs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var tab = session.Tabs[session.Selected];
        return OpenAsync([tab.Path!], tab.Follow);
    }

    /// <summary>Forgets the recent files list.</summary>
    public void ClearRecentFiles() => _recent.Clear();

    /// <summary>Cancels the open in progress (UX-4); the current log stays.</summary>
    public void CancelLoad()
    {
        if (_openCancellation is { } cancellation)
        {
            _openCancellation = null;
            cancellation.Cancel();
            IsLoading = false;
            LoadingText = string.Empty;
        }
    }

    /// <summary>Closes the open log and shows the empty state again.</summary>
    public void Close()
    {
        CancelLoad();
        var log = CurrentLog;
        CurrentLog = null;
        log?.Dispose();
        Notice = null;
        SaveSession();
    }

    /// <summary>Something went wrong that the view could not handle; shown instead of crashing.</summary>
    public void ReportUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Notice = new Notice(NoticeSeverity.Error, "Something went wrong", exception.Message);
    }

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.IsFollowing) && ReferenceEquals(sender, CurrentLog))
        {
            SaveSession();
        }
    }

    private void SaveSession()
    {
        if (!RemembersSession)
        {
            return;
        }

        _session.Save(CurrentLog is { Document.FilePath: { } path } log
            ? new SessionState { Tabs = [new SessionTab { Path = path, Follow = log.IsFollowing }] }
            : new SessionState());
    }

    private static bool IsReadError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static Notice? NoticeFor(LogDocument document, int othersSkipped)
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
