using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Models;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels;

/// <summary>Where the rows of the list changed after the document grew: from <see cref="FirstRow"/>, <see cref="Removed"/> rows went and <see cref="Added"/> came.</summary>
public sealed record RowsChange(int FirstRow, int Removed, int Added);

/// <summary>
/// Progressive loading and live tail (FR-T1 … FR-T7): reads an <see cref="ILogStream"/> and applies its batches to the document and the list on
/// the caller's (UI) thread. Batches are <b>queued</b>, not applied, while a background pass (filter, search) reads the document or while the
/// tail is paused, and applied in order afterwards (FR-T5).
/// </summary>
public sealed partial class LogViewModel : IDisposable
{
    private static readonly TimeSpan ContextRefreshDelay = TimeSpan.FromMilliseconds(300);

    private readonly Queue<StreamBatch> _queue = new();
    private readonly Stopwatch _loadClock = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ILogStream? _stream;
    private CancellationTokenSource? _streamCancellation;
    private int _passDepth;
    private Func<LogEvent, bool>? _activePredicate;
    private bool _contextActive;
    private StreamState _streamState = StreamState.Loading;
    private int _queuedHighWater;
    private bool _contentArrived;

    /// <summary>The first read of the file is in progress; rows appear as they are read.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Fraction of the file read by the first read (0 … 1).</summary>
    [ObservableProperty]
    public partial double LoadProgress { get; private set; }

    /// <summary>Tail (FR-T1): what is written to the file is shown as it arrives.</summary>
    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    /// <summary>FR-T5: incoming events are held back until resumed, so the list stays still while it is read.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>FR-T3: while tailing, move on to the next file of a rolling log.</summary>
    [ObservableProperty]
    public partial bool FollowRolled { get; set; } = true;

    /// <summary>The grid shows the newest event, so new ones scroll into view (set by the view, FR-T2).</summary>
    [ObservableProperty]
    public partial bool FollowLatest { get; set; } = true;

    /// <summary>Events that arrived while the grid was scrolled away from the newest one (FR-T2: "N new events").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingNewEvents), nameof(PendingNewEventsLabel))]
    public partial int PendingNewEvents { get; private set; }

    /// <summary>Reading stopped for good: the user stopped the load, or the file could not be read any more.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh), nameof(CanFollow))]
    public partial bool IsStopped { get; private set; }

    public bool HasPendingNewEvents => PendingNewEvents > 0;

    public string PendingNewEventsLabel
        => string.Create(CultureInfo.InvariantCulture, $"{PendingNewEvents:N0} new event{(PendingNewEvents == 1 ? string.Empty : "s")} — jump to latest");

    /// <summary>Refresh (F5, FR-T7) reads what was written since: possible once the first read has finished, and pointless while tailing.</summary>
    public bool CanRefresh => _stream is not null && !IsStopped && !IsLoading && !IsFollowing;

    /// <summary>Tail can be switched on while the file is still being read from.</summary>
    public bool CanFollow => _stream is not null && !IsStopped;

    /// <summary>Completes when <see cref="RunAsync"/> has stopped reading. Mainly for tests.</summary>
    internal Task Stopped => _stopped.Task;

    /// <summary>How long after the read started the first rows were listed (NFR-2); null until then.</summary>
    public TimeSpan? FirstRowsAfter { get; private set; }

    /// <summary>Events received but not shown yet (paused, or waiting for a background pass).</summary>
    public int BufferedNewEvents => _queue.Count == 0 ? 0 : Math.Max(0, _queuedHighWater - Document.Events.Count);

    /// <summary>Short live state next to the Tail button: "Loading 45 %", "● Live", "Paused — 12 new", "Waiting for app.log…", "Reconnecting…".</summary>
    public string TailStatus
    {
        get
        {
            if (_streamState == StreamState.Reconnecting && !IsStopped)
            {
                return "Reconnecting…"; // a remote host stopped answering (FR-O7); reading resumes where it was
            }

            if (IsLoading)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Loading {LoadProgress:P0}");
            }

            if (_streamState == StreamState.WaitingForFile && !IsStopped)
            {
                return $"Waiting for {Document.FileName}…";
            }

            if (IsPaused)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Paused — {BufferedNewEvents:N0} new");
            }

            return IsFollowing ? "● Live" : string.Empty;
        }
    }

    /// <summary>Raised after new or replaced rows were added to the list without a filter pass; the grid keeps its scroll position.</summary>
    public event EventHandler<RowsChange>? RowsChanged;

    /// <summary>The list was emptied because the file was read again from its start.</summary>
    public event EventHandler? RowsReset;

    /// <summary>The grid should scroll to the newest event.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>The first batch was applied: the log is readable (it may still be loading).</summary>
    public event EventHandler? ContentArrived;

    /// <summary>The first read of the file finished: everything that was there is listed.</summary>
    public event EventHandler? LoadCompleted;

    /// <summary>Something the user should know about: the file was read again, the log rolled over, reading stopped.</summary>
    public event EventHandler<Notice>? NoticeRaised;

    /// <summary>The stream failed: the file could not be read (missing, access denied …).</summary>
    public event EventHandler<Exception>? StreamFailed;

    /// <summary>
    /// Reads <paramref name="stream"/> until it ends, <see cref="Dispose"/> or <see cref="StopLoading"/> is called, or
    /// <paramref name="cancellationToken"/> is cancelled, applying each batch as it arrives. Call it on the UI thread so batches are applied there.
    /// </summary>
    public async Task RunAsync(ILogStream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        stream.Follow = IsFollowing;
        stream.FollowRolled = FollowRolled;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _streamCancellation = cancellation;
        IsLoading = true;
        NotifyStreamProperties();
        _loadClock.Restart();

        try
        {
            await foreach (var batch in stream.ReadAsync(cancellation.Token))
            {
                HandleBatch(batch);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // closed, or the load was stopped: keep what has been read
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            StreamFailed?.Invoke(this, ex);
            if (_contentArrived)
            {
                NoticeRaised?.Invoke(this, new Notice(NoticeSeverity.Error, $"Stopped reading {Document.FileName}", ex.Message));
            }
        }
        finally
        {
            _streamCancellation = null;
            IsLoading = false;
            IsStopped = true;
            IsPaused = false;
            NotifyStreamProperties();
            _stopped.TrySetResult();
        }
    }

    /// <summary>UX-4: stops reading the file. What was read stays listed; tail and refresh are no longer possible.</summary>
    public void StopLoading()
    {
        try
        {
            _streamCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }
    }

    /// <summary>FR-T7: reads what was written to the file since it was last read. A truncated or replaced file is read again from the start.</summary>
    public void Refresh()
    {
        if (CanRefresh)
        {
            _stream!.Refresh();
        }
    }

    /// <summary>Scrolls to the newest event and keeps following it.</summary>
    public void JumpToLatest()
    {
        PendingNewEvents = 0;
        FollowLatest = true;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes one batch from the stream: applied now, or queued while a pass is running or the tail is paused.</summary>
    internal void HandleBatch(StreamBatch batch)
    {
        if (batch is StatusBatch status)
        {
            _streamState = status.State;
            OnPropertyChanged(nameof(TailStatus));
            return;
        }

        // Paused holds back new events only: the first read of the file always completes.
        if (_queue.Count > 0 || _passDepth > 0 || (IsPaused && !IsLoading))
        {
            if (batch is ChunkBatch chunk)
            {
                _queuedHighWater = Math.Max(_queuedHighWater, chunk.Delta.FirstId + chunk.Delta.Events.Count);
            }

            _queue.Enqueue(batch);
            OnPropertyChanged(nameof(TailStatus));
            return;
        }

        Apply(batch);
    }

    public void Dispose()
    {
        _bookmarkService.Changed -= OnBookmarkServiceChanged;
        StopLoading();
        _filterCancellation?.Cancel();
        _queueCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _searchDebounceCancellation?.Cancel();
        _analysisCancellation?.Cancel();
        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            _ = stream.DisposeAsync().AsTask();
        }
    }

    // ------------------------------------------------------------------ property hooks

    partial void OnIsFollowingChanged(bool value)
    {
        if (_stream is not null)
        {
            _stream.Follow = value;
        }

        if (value)
        {
            JumpToLatest(); // like `tail -f`: switching it on shows the end
        }
        else
        {
            IsPaused = false;
        }

        NotifyStreamProperties();
    }

    partial void OnFollowRolledChanged(bool value)
    {
        if (_stream is not null)
        {
            _stream.FollowRolled = value;
        }
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            DrainQueue();
        }

        OnPropertyChanged(nameof(TailStatus));
    }

    partial void OnIsLoadingChanged(bool value) => NotifyStreamProperties();

    partial void OnLoadProgressChanged(double value) => OnPropertyChanged(nameof(TailStatus));

    partial void OnFollowLatestChanged(bool value)
    {
        if (value)
        {
            PendingNewEvents = 0;
        }
    }

    private void NotifyStreamProperties()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(TailStatus));
        OnPropertyChanged(nameof(StatusText));
    }

    // ------------------------------------------------------------------ passes

    /// <summary>A background pass starts reading the document: batches wait until it ends.</summary>
    internal void BeginPass() => _passDepth++;

    internal void EndPass()
    {
        if (_passDepth > 0)
        {
            _passDepth--;
        }

        if (_passDepth == 0)
        {
            DrainQueue();
        }
    }

    private void DrainQueue()
    {
        while (_queue.Count > 0 && _passDepth == 0 && !(IsPaused && !IsLoading))
        {
            Apply(_queue.Dequeue());
        }

        if (_queue.Count == 0)
        {
            _queuedHighWater = 0;
        }

        OnPropertyChanged(nameof(TailStatus));
    }

    // ------------------------------------------------------------------ applying batches

    private void Apply(StreamBatch batch)
    {
        switch (batch)
        {
            case ChunkBatch chunk:
                ApplyChunk(chunk);
                break;
            case ResetBatch reset:
                ApplyReset(reset);
                break;
            case SwitchedBatch switched:
                Document.BeginFile(switched.FilePath);
                OnPropertyChanged(nameof(StatusText));
                NoticeRaised?.Invoke(this, new Notice(NoticeSeverity.Informational, "Following the log", $"The log rolled over; now reading {Path.GetFileName(switched.FilePath)}."));
                FileSwitched?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>A rolling log moved on to its next file; <see cref="LogDocument.FileName"/> changed.</summary>
    public event EventHandler? FileSwitched;

    private void ApplyChunk(ChunkBatch chunk)
    {
        // Events of the first read are the file's existing content, not "new" events: they never scroll the grid or raise "N new events".
        var wasLoading = IsLoading;

        // Taken before the list changes: appending rows makes the grid report "not at the end" at once.
        var following = FollowLatest;

        if (chunk.MergedFiles is { } files)
        {
            Document.AddMergedFiles(files);
            OnPropertyChanged(nameof(IsMerged));
        }

        UpdateFormat(chunk.Format);
        Document.Text.Append(chunk.Text);
        Document.EncodingName = chunk.EncodingName;
        Document.FileSizeBytes = chunk.FileLength;
        var change = Document.Apply(chunk.Delta);
        var (firstRow, removed, added) = View.Sync(_activePredicate, change.FirstChangedIndex);
        if (FirstRowsAfter is null && Document.Events.Count > 0)
        {
            FirstRowsAfter = _loadClock.Elapsed;
        }

        if (change.Removed > 0 || change.Added > 0)
        {
            RefreshLevelCounts();
            ResolveBookmarks(change.FirstChangedIndex);
        }

        if (added > 0 || removed > 0)
        {
            UpdateMatchesAfterSync(firstRow, removed, added);
            OnPropertyChanged(nameof(ShowNoMatches));
            RowsChanged?.Invoke(this, new RowsChange(firstRow, removed, added));
            if (_contextActive)
            {
                QueueApply(ContextRefreshDelay); // the events after a new match are not known until they arrive: expand again
            }

            ScheduleAnalysis(AnalysisThrottle);
        }

        // A regular expression that gave up on the new events (NFR-10) is dropped by a fresh pass, which also says why. Checked even when no row
        // was added: the filter may have left out every new event because its expression gave up.
        if (change.Removed > 0 || change.Added > 0)
        {
            if (_appliedSpec is { } applied && HasSlowExpression(applied))
            {
                QueueApply();
            }
            else if (CurrentMatcher is { TooSlow: true })
            {
                _lastScheduledSearch = RecomputeMatchesAsync();
            }
        }

        if (IsLoading)
        {
            LoadProgress = chunk.FileLength > 0 ? Math.Min(1, (double)chunk.BytesRead / chunk.FileLength) : 0;
        }

        if (!_contentArrived)
        {
            _contentArrived = true;
            ContentArrived?.Invoke(this, EventArgs.Empty);
        }

        if (chunk.InitialLoadComplete)
        {
            Document.LoadTime = _loadClock.Elapsed;
            LoadProgress = 1;
            IsLoading = false;
            ScheduleAnalysis(TimeSpan.Zero); // the statistics wait for the load to finish
            LoadCompleted?.Invoke(this, EventArgs.Empty);
            if (IsFollowing)
            {
                JumpToLatest(); // tail was on from the start (a restored session): show the end once it is there
            }
        }

        OnPropertyChanged(nameof(StatusText));

        var netNew = Math.Max(0, added - removed);
        if (netNew > 0 && !wasLoading)
        {
            if (following)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                PendingNewEvents += netNew;
            }
        }
    }

    private void ApplyReset(ResetBatch reset)
    {
        Document.Clear();
        SelectedEvent = null;
        _matches = [];
        View.SetFilter(_activePredicate is null ? null : []);
        RefreshLevelCounts();
        ResolveBookmarks(0);
        UpdateSummary();
        ScheduleAnalysis(TimeSpan.Zero);

        PendingNewEvents = 0;
        LoadProgress = 0;
        _loadClock.Restart();
        IsLoading = true;

        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(StatusText));
        RowsReset?.Invoke(this, EventArgs.Empty);
        NoticeRaised?.Invoke(this, new Notice(NoticeSeverity.Informational, $"{Document.FileName} was read again", reset.Reason));
    }
}
