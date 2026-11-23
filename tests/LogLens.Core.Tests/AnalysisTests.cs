using LogLens.Core.Analysis;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

/// <summary>Statistics (FR-X1) and the timeline histogram (FR-N2).</summary>
public class AnalysisTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 8, 0, 0, TimeSpan.FromMinutes(330));

    private static LogEvent Event(int id, double seconds, LogLevel level = LogLevel.Information, string message = "hello", string? exception = null)
        => new(id, null, 0, 0, Start.AddSeconds(seconds), level, message, exception);

    private static LogEvent Unparsed(int id) => new(id, null, 0, 0, DateTimeOffset.MinValue, LogLevel.Unparsed, "garbage", null);

    // ------------------------------------------------------------------ statistics

    [Fact]
    public void Statistics_of_nothing_are_empty()
    {
        var stats = LogStatistics.Compute([]);

        Assert.Equal(0, stats.Total);
        Assert.Null(stats.First);
        Assert.Null(stats.Last);
        Assert.Equal(TimeSpan.Zero, stats.Duration);
        Assert.Empty(stats.TopMessages);
    }

    [Fact]
    public void Statistics_count_levels_and_the_time_span()
    {
        LogEvent[] events =
        [
            Event(0, 0, LogLevel.Information),
            Event(1, 30, LogLevel.Error),
            Event(2, 90, LogLevel.Information),
            Event(3, 10, LogLevel.Warning),
        ];

        var stats = LogStatistics.Compute(events);

        Assert.Equal(4, stats.Total);
        Assert.Equal(2, stats.LevelCounts[(int)LogLevel.Information]);
        Assert.Equal(1, stats.LevelCounts[(int)LogLevel.Error]);
        Assert.Equal(1, stats.LevelCounts[(int)LogLevel.Warning]);
        Assert.Equal(Start, stats.First);
        Assert.Equal(Start.AddSeconds(90), stats.Last);
        Assert.Equal(TimeSpan.FromSeconds(90), stats.Duration);
    }

    [Fact]
    public void Unparsed_lines_count_but_do_not_stretch_the_time_span_or_form_groups()
    {
        var stats = LogStatistics.Compute([Event(0, 0), Unparsed(1), Event(2, 5)]);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.UnparsedCount);
        Assert.Equal(Start, stats.First);
        Assert.Equal(TimeSpan.FromSeconds(5), stats.Duration);
        Assert.Equal(1, stats.DistinctMessages);
    }

    [Fact]
    public void Messages_that_differ_only_in_numbers_form_one_group_with_the_highest_level()
    {
        LogEvent[] events =
        [
            Event(0, 0, LogLevel.Information, "Order 41 shipped"),
            Event(1, 1, LogLevel.Information, "Payment failed"),
            Event(2, 2, LogLevel.Warning, "Order 42 shipped"),
            Event(3, 3, LogLevel.Information, "Order 43 shipped"),
        ];

        var stats = LogStatistics.Compute(events);

        var top = stats.TopMessages[0];
        Assert.Equal("Order {n} shipped", top.Key);
        Assert.Equal(3, top.Count);
        Assert.Equal(LogLevel.Warning, top.Level);
        Assert.Equal("Order 41 shipped", top.Sample);
        Assert.Equal(2, stats.DistinctMessages);
    }

    [Fact]
    public void Equal_counts_keep_the_order_the_groups_first_appeared_in_and_the_list_is_cut_to_the_top()
    {
        var events = Enumerable.Range(0, 6).Select(i => Event(i, i, message: "kind-" + (char)('a' + i))).ToArray();

        var stats = LogStatistics.Compute(events, top: 3);

        Assert.Equal(["kind-a", "kind-b", "kind-c"], stats.TopMessages.Select(g => g.Key));
        Assert.Equal(6, stats.DistinctMessages);
    }

    [Fact]
    public void Exceptions_are_grouped_by_their_first_line()
    {
        LogEvent[] events =
        [
            Event(0, 0, LogLevel.Error, "a", "System.TimeoutException: after 30 s\n   at A.B()"),
            Event(1, 1, LogLevel.Error, "b", "System.TimeoutException: after 45 s\n   at C.D()"),
            Event(2, 2, LogLevel.Error, "c", "System.IO.IOException: disk"),
            Event(3, 3),
        ];

        var stats = LogStatistics.Compute(events);

        Assert.Equal(2, stats.DistinctExceptions);
        Assert.Equal(2, stats.TopExceptions[0].Count);
        Assert.Equal("System.TimeoutException: after 30 s", stats.TopExceptions[0].Sample);
    }

    [Fact]
    public void Statistics_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LogStatistics.Compute([Event(0, 0)], cancellationToken: cancellation.Token));
    }

    // ------------------------------------------------------------------ timeline

    [Fact]
    public void A_timeline_of_nothing_is_empty()
    {
        Assert.True(TimelineBuilder.Build([], null).IsEmpty);
        Assert.True(TimelineBuilder.Build([Unparsed(0)], null).IsEmpty);
        Assert.Equal(-1, TimelineData.Empty.BucketAt(DateTime.Now));
    }

    [Fact]
    public void Events_at_one_instant_fill_a_single_bucket()
    {
        var data = TimelineBuilder.Build([Event(0, 0), Event(1, 0, LogLevel.Error)], null);

        Assert.Equal(1, data.BucketCount);
        Assert.Equal(2, data.Totals[0]);
        Assert.Equal(2, data.MaxTotal);
        Assert.Equal(1, data.CountsByLevel[(int)LogLevel.Error][0]);
    }

    [Fact]
    public void Events_are_spread_over_equal_buckets_and_the_last_one_lands_in_the_last_bucket()
    {
        var events = Enumerable.Range(0, 101).Select(i => Event(i, i)).ToArray(); // one event per second for 100 s

        var data = TimelineBuilder.Build(events, null, buckets: 10);

        Assert.Equal(10, data.BucketCount);
        Assert.Equal(101, data.EventCount);
        Assert.Equal(101, data.Totals.Sum());
        Assert.True(data.Totals[^1] >= 1);
        Assert.Equal(TimeRangeStart(events[0]), data.Start);
        Assert.True(data.End >= TimeRangeStart(events[^1]));
    }

    [Fact]
    public void The_timeline_counts_only_what_the_filter_includes_and_skips_unparsed_lines()
    {
        LogEvent[] events = [Event(0, 0), Event(1, 10, LogLevel.Error), Unparsed(2), Event(3, 20, LogLevel.Error)];

        var data = TimelineBuilder.Build(events, e => e.Level == LogLevel.Error, buckets: 2);

        Assert.Equal(2, data.EventCount);
        Assert.Equal(TimeRangeStart(events[1]), data.Start);
        Assert.All(data.CountsByLevel[(int)LogLevel.Information], count => Assert.Equal(0, count));
    }

    [Fact]
    public void Bucket_lookup_is_clamped_to_the_range_and_maps_times_back()
    {
        var data = TimelineBuilder.Build([Event(0, 0), Event(1, 100)], null, buckets: 10);

        Assert.Equal(0, data.BucketAt(data.Start.AddDays(-1)));
        Assert.Equal(9, data.BucketAt(data.End.AddDays(1)));
        Assert.Equal(data.Start, data.TimeAt(-1));
        Assert.Equal(data.End, data.TimeAt(2));
        Assert.Equal(data.BucketStart(5), data.Start + (data.BucketSpan * 5));
    }

    private static DateTime TimeRangeStart(LogEvent e) => e.Timestamp.DateTime;
}
