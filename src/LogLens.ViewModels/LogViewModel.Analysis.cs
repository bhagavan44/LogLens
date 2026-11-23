using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Analysis;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>The timeline histogram (FR-N2) and the statistics panel (FR-X1, FR-X2). Both are computed off the UI thread and refreshed as the list changes.</summary>
public sealed partial class LogViewModel
{
    /// <summary>While events keep arriving the analysis is recomputed at most this often.</summary>
    private static readonly TimeSpan AnalysisThrottle = TimeSpan.FromMilliseconds(500);

    private CancellationTokenSource? _analysisCancellation;
    private bool _analysisPending;
    private Task _lastAnalysis = Task.CompletedTask;
    private Func<LogEvent, bool>? _timelinePredicate;

    /// <summary>The histogram above the grid is shown (and kept up to date).</summary>
    [ObservableProperty]
    public partial bool TimelineVisible { get; set; }

    /// <summary>Events over time, stacked by level, for everything the filters leave except the time window itself.</summary>
    [ObservableProperty]
    public partial TimelineData Timeline { get; private set; } = TimelineData.Empty;

    /// <summary>The statistics panel is shown (and kept up to date).</summary>
    [ObservableProperty]
    public partial bool StatisticsVisible { get; set; }

    /// <summary>Summary of the events currently listed (all filters applied).</summary>
    [ObservableProperty]
    public partial LogStatistics Statistics { get; private set; } = LogStatistics.Empty;

    partial void OnTimelineVisibleChanged(bool value)
    {
        if (value)
        {
            ScheduleAnalysis(TimeSpan.Zero);
        }
    }

    partial void OnStatisticsVisibleChanged(bool value)
    {
        if (value)
        {
            ScheduleAnalysis(TimeSpan.Zero);
        }
    }

    partial void OnStatisticsChanged(LogStatistics value)
    {
        OnPropertyChanged(nameof(StatisticsSummary));
        OnPropertyChanged(nameof(StatisticsLevelLine));
        OnPropertyChanged(nameof(HasNoExceptionStats));
    }

    /// <summary>True when the listed events carry no exceptions (the panel says so instead of showing an empty list).</summary>
    public bool HasNoExceptionStats => Statistics.TopExceptions.Count == 0;

    /// <summary>"12,345 events  ·  2026-11-13 08:00:00.012 +05:30 → 08:20:00.000  (20 min)".</summary>
    public string StatisticsSummary
    {
        get
        {
            var s = Statistics;
            if (s.Total == 0)
            {
                return "No events";
            }

            var text = string.Create(CultureInfo.InvariantCulture, $"{s.Total:N0} event{(s.Total == 1 ? string.Empty : "s")}");
            if (s.First is { } first && s.Last is { } last)
            {
                text += $"  ·  {LogText.FullTime(first)} → {last.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  ({FormatDuration(s.Duration)})";
            }

            return text;
        }
    }

    /// <summary>"3 errors · 12 warnings · 4,100 information": only the levels that occur.</summary>
    public string StatisticsLevelLine
    {
        get
        {
            var counts = Statistics.LevelCounts;
            var parts = new List<string>();
            AddLevel(parts, counts, LogLevel.Fatal, "fatal", "fatal");
            AddLevel(parts, counts, LogLevel.Error, "error", "errors");
            AddLevel(parts, counts, LogLevel.Warning, "warning", "warnings");
            AddLevel(parts, counts, LogLevel.Information, "information", "information");
            AddLevel(parts, counts, LogLevel.Debug, "debug", "debug");
            AddLevel(parts, counts, LogLevel.Verbose, "verbose", "verbose");
            AddLevel(parts, counts, LogLevel.Unknown, "without a level", "without a level");
            AddLevel(parts, counts, LogLevel.Unparsed, "unparsed line", "unparsed lines");
            return string.Join("  ·  ", parts);
        }
    }

    // ------------------------------------------------------------------ time window from the timeline (FR-N2)

    /// <summary>Applies a time window picked by dragging on the timeline (wall-clock times as shown in the grid).</summary>
    public void SelectTimeRange(DateTime from, DateTime to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        SetTimeBoxes(new TimeRange(from, to));
    }

    // ------------------------------------------------------------------ rows of the statistics panel (FR-X2)

    /// <summary>Show only the events that share <paramref name="group"/>'s message shape.</summary>
    public void ShowOnlyMessage(StatGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ActivePin = EventPin.ForMessage(group.Key);
        QueueApply();
    }

    /// <summary>Show only the events that share <paramref name="group"/>'s exception.</summary>
    public void ShowOnlyException(StatGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ActivePin = EventPin.ForException(group.Key);
        QueueApply();
    }

    // ------------------------------------------------------------------ scheduling

    /// <summary>
    /// Brings the timeline and statistics up to date. Immediate requests (a filter changed) replace anything waiting; throttled ones
    /// (events arriving) are dropped when a run is already waiting, because that run will see the latest events anyway.
    /// </summary>
    private void ScheduleAnalysis(TimeSpan delay)
    {
        if (!TimelineVisible && !StatisticsVisible)
        {
            return;
        }

        if (delay > TimeSpan.Zero && _analysisPending)
        {
            return;
        }

        _analysisCancellation?.Cancel();
        var cancellation = _analysisCancellation = new CancellationTokenSource();
        _analysisPending = true;
        _lastAnalysis = RunAnalysisAsync(delay, cancellation.Token);
    }

    private async Task RunAnalysisAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            _analysisPending = false;
            token.ThrowIfCancellationRequested();

            if (TimelineVisible)
            {
                var events = Document.Events;
                var predicate = _timelinePredicate;
                TimelineData data;
                BeginPass(); // the document must not grow under the worker
                try
                {
                    data = await Task.Run(() => TimelineBuilder.Build(events, predicate, TimelineBuilder.DefaultBuckets, token), token);
                }
                finally
                {
                    EndPass();
                }

                token.ThrowIfCancellationRequested();
                Timeline = data;
            }

            // Statistics read every message, so they wait until the file has been loaded.
            if (StatisticsVisible && !IsLoading)
            {
                var snapshot = View.Chronological.ToArray(); // the list itself can be re-filtered while the worker reads it
                LogStatistics stats;
                BeginPass();
                try
                {
                    stats = await Task.Run(() => LogStatistics.Compute(snapshot, LogStatistics.DefaultTop, token), token);
                }
                finally
                {
                    EndPass();
                }

                token.ThrowIfCancellationRequested();
                Statistics = stats;
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request
        }
    }

    private static void AddLevel(List<string> parts, int[] counts, LogLevel level, string singular, string plural)
    {
        var count = counts[(int)level];
        if (count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? singular : plural)}"));
        }
    }

    private static string FormatDuration(TimeSpan span) => span.TotalSeconds switch
    {
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{span.TotalMilliseconds:N0} ms"),
        < 120 => string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:N0} s"),
        < 7200 => string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:N0} min"),
        < 172800 => string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:N1} h"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:N1} days"),
    };
}
