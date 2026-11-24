using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>
/// Everything that narrows the list of events: levels (FR-S1), time window (FR-S2), "only matches" of the find bar (FR-S3), the query
/// (FR-S4) and the quick filters (FR-S5). Conditions are ANDed. Format-agnostic: it only looks at <see cref="LogEvent"/>.
/// </summary>
public sealed record FilterSpec
{
    public int Levels { get; init; } = LevelMask.All;

    public TimeRange Time { get; init; } = TimeRange.Open;

    /// <summary>The zone <see cref="Time"/> is read in: the one the grid shows (FR-G5).</summary>
    public TimestampZone Zone { get; init; } = TimestampZone.AsLogged;

    public CompiledQuery? Query { get; init; }

    /// <summary>When set, only events matching the find-bar text are kept ("Only matches").</summary>
    public SearchMatcher? OnlyMatching { get; init; }

    /// <summary>When set, only events in this group are kept ("Show only events like this one").</summary>
    public EventPin? Pin { get; init; }

    /// <summary>Groups hidden from a row's menu ("Hide events like this one"): an event in any of them is left out.</summary>
    public IReadOnlyList<EventPin> Exclude { get; init; } = [];

    /// <summary>The hide rules in force (FR-S6): an event matching any of them is left out.</summary>
    public IReadOnlyList<CompiledQuery> Hide { get; init; } = [];

    /// <summary>True when at least one condition removes events.</summary>
    public bool IsActive => Levels != LevelMask.All || !Time.IsOpen || Query is not null || OnlyMatching is not null || Pin is not null
                            || Exclude.Count > 0 || Hide.Count > 0;

    /// <summary>The combined predicate, or null when nothing is filtered (the caller can then show every event without scanning).</summary>
    /// <remarks>The predicate is thread-safe, so a filter pass can run it on a worker thread.</remarks>
    public Func<LogEvent, bool>? BuildPredicate()
    {
        if (!IsActive)
        {
            return null;
        }

        var levels = Levels;
        var checkLevels = levels != LevelMask.All;
        var time = Time.IsOpen ? null : Time;
        var query = Query?.Predicate;
        var matcher = OnlyMatching;
        var pin = Pin?.Predicate;
        var exclude = Exclude.Select(x => x.Predicate).Concat(Hide.Select(h => h.Predicate)).ToArray();
        var zone = Zone;

        return e =>
        {
            if (checkLevels && !LevelMask.Allows(levels, e.Level))
            {
                return false;
            }

            if (time is not null && (e.IsUnparsed || !time.Contains(TimeRange.WallClock(e.Timestamp, zone))))
            {
                return false;
            }

            foreach (var hidden in exclude)
            {
                if (hidden(e))
                {
                    return false;
                }
            }

            if (pin is not null && !pin(e))
            {
                return false;
            }

            if (query is not null && !query(e))
            {
                return false;
            }

            return matcher is null || matcher.IsMatch(e);
        };
    }
}
