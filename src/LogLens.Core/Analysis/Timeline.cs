using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.Core.Analysis;

/// <summary>
/// Event counts over time, stacked by level: what the timeline histogram above the grid draws (FR-N2). Times are wall-clock readings, the
/// same ones the time filter and the grid use, so a bar and a time-window boundary line up.
/// </summary>
public sealed class TimelineData
{
    public static TimelineData Empty { get; } = new(DateTime.MinValue, TimeSpan.FromSeconds(1), 0, [], []);

    internal TimelineData(DateTime start, TimeSpan bucketSpan, int bucketCount, int[][] countsByLevel, int[] totals)
    {
        Start = start;
        BucketSpan = bucketSpan;
        BucketCount = bucketCount;
        CountsByLevel = countsByLevel;
        Totals = totals;
        MaxTotal = totals.Length == 0 ? 0 : totals.Max();
        EventCount = totals.Sum();
    }

    /// <summary>Start of the first bucket.</summary>
    public DateTime Start { get; }

    public TimeSpan BucketSpan { get; }

    public int BucketCount { get; }

    /// <summary>End of the last bucket.</summary>
    public DateTime End => Start + (BucketSpan * BucketCount);

    /// <summary><c>CountsByLevel[level][bucket]</c>, indexed by the numeric <see cref="LogLevel"/> (0 = unknown).</summary>
    public int[][] CountsByLevel { get; }

    /// <summary>Events per bucket, all levels together.</summary>
    public int[] Totals { get; }

    /// <summary>The largest bucket total (the height the bars are scaled to).</summary>
    public int MaxTotal { get; }

    /// <summary>Number of events with a timestamp that the histogram covers.</summary>
    public int EventCount { get; }

    public bool IsEmpty => BucketCount == 0;

    public DateTime BucketStart(int bucket) => Start + (BucketSpan * bucket);

    /// <summary>The bucket containing <paramref name="time"/>, clamped to the histogram's range; -1 for an empty histogram.</summary>
    public int BucketAt(DateTime time)
    {
        if (BucketCount == 0)
        {
            return -1;
        }

        var index = (time - Start).Ticks / BucketSpan.Ticks;
        return (int)Math.Clamp(index, 0, BucketCount - 1);
    }

    /// <summary>The time at <paramref name="fraction"/> (0 … 1) across the histogram.</summary>
    public DateTime TimeAt(double fraction)
        => Start + TimeSpan.FromTicks((long)(Math.Clamp(fraction, 0, 1) * (End - Start).Ticks));
}

public static class TimelineBuilder
{
    public const int DefaultBuckets = 120;

    /// <summary>
    /// Counts the events accepted by <paramref name="include"/> (null = all) into <paramref name="buckets"/> equal slices of the time they
    /// span. Unparsed lines have no timestamp and are skipped. Safe to call off the UI thread while nothing mutates the list.
    /// </summary>
    public static TimelineData Build(
        IReadOnlyList<LogEvent> events,
        Func<LogEvent, bool>? include,
        int buckets = DefaultBuckets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        buckets = Math.Max(1, buckets);

        // One pass to collect the timestamps (and find the span), a second cheap one to count.
        var count = events.Count;
        var ticks = new long[Math.Max(16, Math.Min(count, 1 << 20))];
        var levels = new byte[ticks.Length];
        var used = 0;
        long min = long.MaxValue, max = long.MinValue;

        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var e = events[i];
            if (e.IsUnparsed || (include is not null && !include(e)))
            {
                continue;
            }

            var wall = TimeRange.WallClock(e.Timestamp).Ticks;
            if (used == ticks.Length)
            {
                Array.Resize(ref ticks, ticks.Length * 2);
                Array.Resize(ref levels, levels.Length * 2);
            }

            ticks[used] = wall;
            levels[used] = (byte)e.Level;
            used++;
            min = Math.Min(min, wall);
            max = Math.Max(max, wall);
        }

        if (used == 0)
        {
            return TimelineData.Empty;
        }

        var span = max - min;
        int bucketCount;
        long bucketTicks;
        if (span == 0)
        {
            bucketCount = 1;
            bucketTicks = TimeSpan.TicksPerSecond;
        }
        else
        {
            bucketCount = buckets;
            bucketTicks = Math.Max(1, (span + buckets - 1) / buckets); // rounded up so the last event falls inside the last bucket
        }

        var byLevel = new int[Enum.GetValues<LogLevel>().Length][];
        for (var l = 0; l < byLevel.Length; l++)
        {
            byLevel[l] = new int[bucketCount];
        }

        var totals = new int[bucketCount];
        for (var i = 0; i < used; i++)
        {
            var bucket = (int)Math.Min(bucketCount - 1, (ticks[i] - min) / bucketTicks);
            byLevel[levels[i]][bucket]++;
            totals[bucket]++;
        }

        return new TimelineData(new DateTime(min), TimeSpan.FromTicks(bucketTicks), bucketCount, byLevel, totals);
    }
}
