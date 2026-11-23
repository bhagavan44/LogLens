using LogLens.Core.Models;

namespace LogLens.Core.Analysis;

/// <summary>A set of events that share a normalised message (or exception): what the statistics panel lists.</summary>
/// <param name="Key">The normalised text, e.g. <c>Order {n} shipped to customer {n}</c>.</param>
/// <param name="Sample">The first event's real first line, shown as a tooltip.</param>
/// <param name="Count">How many events share the key.</param>
/// <param name="Level">The most severe level among them.</param>
public sealed record StatGroup(string Key, string Sample, int Count, LogLevel Level);

/// <summary>Summary of a set of events (FR-X1): totals, levels, time span and the most frequent messages and exceptions.</summary>
public sealed class LogStatistics
{
    public const int DefaultTop = 10;

    public static LogStatistics Empty { get; } = new();

    public int Total { get; private init; }

    /// <summary>Events per level, indexed by the numeric <see cref="LogLevel"/>.</summary>
    public int[] LevelCounts { get; private init; } = new int[Enum.GetValues<LogLevel>().Length];

    public int UnparsedCount { get; private init; }

    public DateTimeOffset? First { get; private init; }

    public DateTimeOffset? Last { get; private init; }

    public TimeSpan Duration => First is { } f && Last is { } l ? l - f : TimeSpan.Zero;

    public IReadOnlyList<StatGroup> TopMessages { get; private init; } = [];

    public IReadOnlyList<StatGroup> TopExceptions { get; private init; } = [];

    /// <summary>How many different normalised messages there are (the list above shows only the top ones).</summary>
    public int DistinctMessages { get; private init; }

    public int DistinctExceptions { get; private init; }

    /// <summary>
    /// Computes the statistics of <paramref name="events"/>. Safe off the UI thread while nothing mutates the list. Events without a
    /// timestamp (unparsed lines) count towards the totals but not the time span or the message groups.
    /// </summary>
    public static LogStatistics Compute(IReadOnlyList<LogEvent> events, int top = DefaultTop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var levels = new int[Enum.GetValues<LogLevel>().Length];
        var messages = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var exceptions = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var unparsed = 0;
        DateTimeOffset? first = null, last = null;

        for (var i = 0; i < events.Count; i++)
        {
            if ((i & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var e = events[i];
            levels[(int)e.Level]++;

            if (e.IsUnparsed)
            {
                unparsed++;
                continue;
            }

            if (first is null || e.Timestamp < first)
            {
                first = e.Timestamp;
            }

            if (last is null || e.Timestamp > last)
            {
                last = e.Timestamp;
            }

            Add(messages, MessageNormalizer.Normalize(e.Message), e, LogText.FirstLine(e.Message));
            if (e.Exception is { } exception)
            {
                Add(exceptions, MessageNormalizer.Normalize(exception), e, LogText.FirstLine(exception));
            }
        }

        return new LogStatistics
        {
            Total = events.Count,
            LevelCounts = levels,
            UnparsedCount = unparsed,
            First = first,
            Last = last,
            TopMessages = Top(messages, top),
            TopExceptions = Top(exceptions, top),
            DistinctMessages = messages.Count,
            DistinctExceptions = exceptions.Count,
        };
    }

    private static void Add(Dictionary<string, Accumulator> groups, string key, LogEvent e, string sample)
    {
        if (!groups.TryGetValue(key, out var acc))
        {
            acc = new Accumulator(sample, e.Id);
            groups[key] = acc;
        }

        acc.Count++;

        // Unparsed lines never get here, so the numeric order is the severity order (Unknown is the lowest).
        if (e.Level > acc.Level)
        {
            acc.Level = e.Level;
        }
    }

    // Most frequent first; ties keep the order the groups first appeared in, so the result is deterministic.
    private static StatGroup[] Top(Dictionary<string, Accumulator> groups, int top)
        => groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Value.FirstId)
            .Take(top)
            .Select(g => new StatGroup(g.Key, g.Value.Sample, g.Value.Count, g.Value.Level))
            .ToArray();

    private sealed class Accumulator(string sample, int firstId)
    {
        public string Sample { get; } = sample;

        public int FirstId { get; } = firstId;

        public int Count { get; set; }

        public LogLevel Level { get; set; }
    }
}
