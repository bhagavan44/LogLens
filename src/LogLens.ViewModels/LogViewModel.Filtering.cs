using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Collections;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>Filtering: level toggles (FR-S1), the time window (FR-S2), the query bar (FR-S4) and the quick filters (FR-S5).</summary>
public sealed partial class LogViewModel
{
    private const string WallFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private static readonly TimeSpan QueryDebounce = TimeSpan.FromMilliseconds(300);

    private static readonly string[] MinLevelLabels = ["All levels", "Debug and above", "Information and above", "Warning and above", "Error and above"];
    private static readonly LogLevel[] MinLevels = [LogLevel.Unknown, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];

    private readonly List<EventPin> _hiddenGroups = [];
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _queueCancellation;
    private Task _lastQueuedApply = Task.CompletedTask;
    private CompiledQuery? _lastGoodQuery;
    private TimeRange _timeRange = TimeRange.Open;
    private bool _suspendApply;
    private bool _syncingLevels;

    /// <summary>Raised after a filter pass swapped in a new list; carries the event that was active before, so the view can keep it in sight.</summary>
    public event EventHandler<LogEvent?>? FilterApplied;

    public ObservableCollection<LevelToggle> LevelToggles { get; } = [];

    public IReadOnlyList<string> MinLevelOptions => MinLevelLabels;

    /// <summary>Index into <see cref="MinLevelOptions"/>; -1 when the toggles match no preset ("custom").</summary>
    [ObservableProperty]
    public partial int MinLevelIndex { get; set; }

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    /// <summary>Why the query does not compile, with its position; null when it is valid or empty. The last valid query stays applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueryError))]
    public partial string? QueryError { get; private set; }

    public bool HasQueryError => QueryError is not null;

    [ObservableProperty]
    public partial string TimeFromText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeToText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeError))]
    public partial string? TimeError { get; private set; }

    public bool HasTimeError => TimeError is not null;

    /// <summary>A filter pass is running on a worker thread.</summary>
    [ObservableProperty]
    public partial bool IsFiltering { get; private set; }

    /// <summary>At least one filter removes events.</summary>
    [ObservableProperty]
    public partial bool HasActiveFilters { get; private set; }

    /// <summary>How long the last filter pass took, or null when nothing is filtered.</summary>
    public double? LastFilterMilliseconds { get; private set; }

    /// <summary>The time window currently applied (open ends = no limit).</summary>
    public TimeRange TimeWindow => _timeRange;

    public bool IsTimeFiltered => !_timeRange.IsOpen;

    public string TimeButtonLabel => _timeRange.IsOpen ? "Time" : $"Time: {Short(_timeRange.From)} – {Short(_timeRange.To)}";

    /// <summary>"Show only events like this one" (FR-S5); null when no group is pinned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePin), nameof(ActivePinLabel))]
    public partial EventPin? ActivePin { get; private set; }

    public bool HasActivePin => ActivePin is not null;

    public string ActivePinLabel => ActivePin is { } pin ? $"Only: {pin.Label}" : string.Empty;

    /// <summary>"Hide events like this one" has hidden at least one kind of event (FR-S5).</summary>
    public bool HasHiddenGroups => _hiddenGroups.Count > 0;

    public string HiddenGroupsLabel => _hiddenGroups.Count switch
    {
        0 => string.Empty,
        1 => $"Hidden: {_hiddenGroups[0].Label}",
        var n => string.Create(CultureInfo.InvariantCulture, $"Hidden: {n} kinds of event"),
    };

    /// <summary>Every hidden kind of event, one per line (the chip's tooltip).</summary>
    public string HiddenGroupsList => string.Join("\n", _hiddenGroups.Select(g => g.Label));

    private void InitFilters()
    {
        foreach (var level in new[] { LogLevel.Verbose, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Fatal })
        {
            LevelToggles.Add(new LevelToggle(level, Document.CountOf(level), OnLevelToggled));
        }

        // Only offer buttons for events without a severity when there are some.
        foreach (var level in new[] { LogLevel.Unknown, LogLevel.Unparsed })
        {
            if (Document.CountOf(level) > 0)
            {
                LevelToggles.Add(new LevelToggle(level, Document.CountOf(level), OnLevelToggled));
            }
        }

        MinLevelIndex = 0;
    }

    // ------------------------------------------------------------------ levels (FR-S1)

    /// <summary>True when there is a level button for <paramref name="level"/>.</summary>
    public bool CanFilterByLevel(LogLevel level) => LevelToggles.Any(t => t.Level == level);

    /// <summary>Quick filter: switches every level off except <paramref name="level"/>.</summary>
    public void ShowOnlyLevel(LogLevel level) => SetLevels(level, t => t.Level == level);

    /// <summary>Quick filter: switches <paramref name="level"/> off and leaves the other levels as they are.</summary>
    public void HideLevel(LogLevel level) => SetLevels(level, t => t.Level != level && t.IsOn);

    private void SetLevels(LogLevel level, Func<LevelToggle, bool> on)
    {
        if (!CanFilterByLevel(level))
        {
            return;
        }

        foreach (var t in LevelToggles)
        {
            t.SetSilently(on(t));
        }

        SyncPresetFromToggles();
        QueueApply();
    }

    private int CurrentLevelMask()
    {
        var mask = LevelMask.All;
        foreach (var t in LevelToggles)
        {
            mask = LevelMask.With(mask, t.Level, t.IsOn);
        }

        return mask;
    }

    private void OnLevelToggled(LevelToggle toggle)
    {
        SyncPresetFromToggles();
        QueueApply();
    }

    private static bool InPreset(LogLevel level, LogLevel minimum)
        => minimum == LogLevel.Unknown || (LevelMask.HasSeverity(level) && level >= minimum);

    private void SyncPresetFromToggles()
    {
        var index = -1;
        for (var i = 0; i < MinLevels.Length && index < 0; i++)
        {
            if (LevelToggles.All(t => t.IsOn == InPreset(t.Level, MinLevels[i])))
            {
                index = i;
            }
        }

        _syncingLevels = true;
        try
        {
            MinLevelIndex = index;
        }
        finally
        {
            _syncingLevels = false;
        }
    }

    partial void OnMinLevelIndexChanged(int value)
    {
        if (_syncingLevels || value < 0 || value >= MinLevels.Length)
        {
            return;
        }

        foreach (var t in LevelToggles)
        {
            t.SetSilently(InPreset(t.Level, MinLevels[value]));
        }

        QueueApply();
    }

    // ------------------------------------------------------------------ query bar (FR-S4)

    partial void OnQueryTextChanged(string value) => QueueApply(QueryDebounce);

    /// <summary>Applies the query now (Enter) instead of after the typing pause.</summary>
    public void ApplyQueryNow() => QueueApply();

    // ------------------------------------------------------------------ time window (FR-S2)

    /// <summary>Applies the From / To boxes; an empty box is an open end. Returns false, with <see cref="TimeError"/> set, when a box can't be read.</summary>
    public bool ApplyTimeBoxes()
    {
        var reference = FirstTimestamp();
        if (!TryParseTimeBox(TimeFromText, reference, "From", out var from) || !TryParseTimeBox(TimeToText, reference, "To", out var to))
        {
            return false;
        }

        if (from is not null && to is not null && from > to)
        {
            TimeError = "\"From\" is later than \"To\".";
            return false;
        }

        TimeError = null;
        SetTimeRange(new TimeRange(from, to));
        return true;
    }

    /// <summary>The last <paramref name="span"/> of the log: the window ends at the last event, not at the current time.</summary>
    public void SetLast(TimeSpan span)
    {
        if (LastTimestamp() is { } end)
        {
            SetTimeBoxes(TimeRange.Last(end, span));
        }
    }

    /// <summary>± <paramref name="radius"/> around the selected event. Returns false, with <see cref="TimeError"/> set, when nothing timed is selected.</summary>
    public bool SetAround(TimeSpan radius)
    {
        if (SelectedEvent is not { IsUnparsed: false } e)
        {
            TimeError = "Select an event with a timestamp first.";
            return false;
        }

        SetTimeBoxes(TimeRange.Around(TimeRange.WallClock(e.Timestamp), radius));
        return true;
    }

    public void ClearTime()
    {
        TimeFromText = string.Empty;
        TimeToText = string.Empty;
        TimeError = null;
        SetTimeRange(TimeRange.Open);
    }

    private void SetTimeBoxes(TimeRange range)
    {
        TimeFromText = range.From?.ToString(WallFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        TimeToText = range.To?.ToString(WallFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        TimeError = null;
        SetTimeRange(range);
    }

    private void SetTimeRange(TimeRange range)
    {
        _timeRange = range;
        OnPropertyChanged(nameof(TimeWindow));
        OnPropertyChanged(nameof(IsTimeFiltered));
        OnPropertyChanged(nameof(TimeButtonLabel));
        QueueApply();
    }

    private bool TryParseTimeBox(string text, DateTime? reference, string name, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (TimeLiteral.TryParse(text, reference, out var parsed) && parsed.HasDate)
        {
            value = parsed.Instant is { } instant ? TimeRange.WallClock(instant) : parsed.Wall;
            return true;
        }

        TimeError = $"\"{name}\" isn't a time. Use e.g. 2026-11-13 08:00:05 (or just 08:00:05).";
        return false;
    }

    private DateTime? FirstTimestamp()
        => Document.Events.FirstOrDefault(e => !e.IsUnparsed) is { } first ? TimeRange.WallClock(first.Timestamp) : null;

    private DateTime? LastTimestamp()
        => Document.Events.LastOrDefault(e => !e.IsUnparsed) is { } last ? TimeRange.WallClock(last.Timestamp) : null;

    private static string Short(DateTime? value) => value?.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "…";

    // ------------------------------------------------------------------ quick filters (FR-S5)

    /// <summary>Shows only events whose message has the same shape as <paramref name="e"/>'s (numbers, ids and quoted values ignored).</summary>
    public void ShowOnlyLike(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (EventPin.Like(e) is { } pin)
        {
            ActivePin = pin;
            QueueApply();
        }
    }

    /// <summary>Removes the "show only events like this one" filter.</summary>
    public void ClearPin()
    {
        if (ActivePin is not null)
        {
            ActivePin = null;
            QueueApply();
        }
    }

    /// <summary>Leaves out every event whose message has the same shape as <paramref name="e"/>'s; adds to what is already hidden.</summary>
    public void HideLike(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (EventPin.Like(e) is not { } pin || _hiddenGroups.Any(g => g.Label == pin.Label))
        {
            return;
        }

        _hiddenGroups.Add(pin);
        OnHiddenGroupsChanged();
    }

    /// <summary>Shows the kinds of event hidden with <see cref="HideLike"/> again.</summary>
    public void ClearHiddenGroups()
    {
        if (_hiddenGroups.Count > 0)
        {
            _hiddenGroups.Clear();
            OnHiddenGroupsChanged();
        }
    }

    private void OnHiddenGroupsChanged()
    {
        OnPropertyChanged(nameof(HasHiddenGroups));
        OnPropertyChanged(nameof(HiddenGroupsLabel));
        OnPropertyChanged(nameof(HiddenGroupsList));
        QueueApply();
    }

    // ------------------------------------------------------------------ reset

    /// <summary>Removes every filter (levels, query, time window, only matches, quick filters) with a single filter pass.</summary>
    public void ClearFilters()
    {
        _suspendApply = true;
        try
        {
            MinLevelIndex = 0;
            foreach (var t in LevelToggles)
            {
                t.SetSilently(true);
            }

            QueryText = string.Empty;
            ClearTime();
            OnlyMatches = false;
            ActivePin = null;
            ClearHiddenGroups();
        }
        finally
        {
            _suspendApply = false;
        }

        QueueApply();
    }

    // ------------------------------------------------------------------ applying

    private FilterSpec BuildSpec()
    {
        // While the query is invalid (e.g. half typed) the last valid one keeps applying, so the list does not flicker and the other
        // filters still take effect.
        CompiledQuery? query;
        if (string.IsNullOrWhiteSpace(QueryText))
        {
            QueryError = null;
            query = _lastGoodQuery = null;
        }
        else if (QueryParser.TryParse(QueryText, out query, out var error))
        {
            QueryError = null;
            _lastGoodQuery = query;
        }
        else
        {
            QueryError = string.Create(CultureInfo.InvariantCulture, $"{error.Message}  (at position {error.Position + 1})");
            query = _lastGoodQuery;
        }

        return new FilterSpec
        {
            Levels = CurrentLevelMask(),
            Time = _timeRange,
            Query = query,
            OnlyMatching = OnlyMatches ? CurrentMatcher : null,
            Pin = ActivePin,
            Exclude = [.. _hiddenGroups],
        };
    }

    private void QueueApply(TimeSpan? delay = null)
    {
        if (_suspendApply)
        {
            return;
        }

        _queueCancellation?.Cancel();
        var cancellation = _queueCancellation = new CancellationTokenSource();
        _lastQueuedApply = RunQueuedApplyAsync(delay ?? TimeSpan.Zero, cancellation.Token);
    }

    private async Task RunQueuedApplyAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            await ApplyFiltersAsync();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change, which applies its own pass
        }
    }

    /// <summary>Completes when every queued filter and search pass (debounce delays included) has finished. Mainly for tests.</summary>
    public async Task WhenSettledAsync()
    {
        while (true)
        {
            var apply = _lastQueuedApply;
            var search = _lastScheduledSearch;
            await Task.WhenAll(apply, search);
            if (ReferenceEquals(apply, _lastQueuedApply) && ReferenceEquals(search, _lastScheduledSearch))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Evaluates every filter against all events on a worker thread (FR-S8), then swaps the result into <see cref="Events"/>. A newer pass
    /// cancels this one, and always applies its own result, so the list never stays half-filtered.
    /// </summary>
    public async Task ApplyFiltersAsync()
    {
        _filterCancellation?.Cancel();
        var cancellation = _filterCancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        try
        {
            var spec = BuildSpec();
            var predicate = spec.BuildPredicate();
            IsFiltering = predicate is not null;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // "Only matches" with context: the matches plus the events around them, counted among the events the other filters leave.
            var matcher = spec.OnlyMatching;
            var context = matcher is not null ? ContextEvents : 0;
            var contextBase = context > 0 ? (spec with { OnlyMatching = null }).BuildPredicate() : null;

            int[]? indices = null;
            if (predicate is not null)
            {
                var events = Document.Events;
                indices = await Task.Run(
                    () => context > 0 && matcher is not null
                        ? ContextExpander.Compute(events, contextBase, matcher.IsMatch, context, token)
                        : LogEventView.ComputeIndices(events, predicate, token),
                    token);
            }

            token.ThrowIfCancellationRequested();

            var keep = SelectedEvent;
            View.SetFilter(indices);
            LastFilterMilliseconds = spec.IsActive ? clock.Elapsed.TotalMilliseconds : null;
            HasActiveFilters = spec.IsActive;
            OnPropertyChanged(nameof(Events));
            OnPropertyChanged(nameof(StatusText));
            FilterApplied?.Invoke(this, keep);

            await RecomputeMatchesAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // a newer pass replaced this one
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
            {
                IsFiltering = false;
            }
        }
    }
}
