using System.Globalization;
using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>Next / previous match navigation over a sorted array of matching rows (F3 / Shift+F3, FR-S3).</summary>
public static class MatchNavigator
{
    /// <summary>
    /// The match after (or before) row <paramref name="current"/>, wrapping around at the ends; -1 when there are no matches.
    /// <paramref name="current"/> may be -1 (nothing selected) and need not itself be a match.
    /// </summary>
    public static int Step(int[] matches, int current, bool forward)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Length == 0)
        {
            return -1;
        }

        var pos = Array.BinarySearch(matches, current); // >= 0: found; < 0: ~insertion point
        if (forward)
        {
            var next = pos >= 0 ? pos + 1 : ~pos;
            return matches[next >= matches.Length ? 0 : next];
        }

        var prev = pos >= 0 ? pos - 1 : ~pos - 1;
        return matches[prev < 0 ? matches.Length - 1 : prev];
    }

    /// <summary>1-based position of <paramref name="current"/> among the matches (0 when it isn't a match).</summary>
    public static int PositionOf(int[] matches, int current)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var pos = Array.BinarySearch(matches, current);
        return pos >= 0 ? pos + 1 : 0;
    }
}

/// <summary>Jumps to the next / previous row that satisfies a condition, e.g. the next warning or worse (FR-N3).</summary>
public static class RowFinder
{
    /// <summary>
    /// The first row after (or before) <paramref name="from"/> for which <paramref name="predicate"/> is true, wrapping around at the ends;
    /// -1 when no other row qualifies. <paramref name="from"/> may be -1 (nothing selected).
    /// </summary>
    public static int Step(IReadOnlyList<LogEvent> rows, int from, bool forward, Func<LogEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(predicate);
        var count = rows.Count;
        if (count == 0)
        {
            return -1;
        }

        // Nothing selected: start just before the first row going forward, just after the last going back.
        var hasStart = from >= 0 && from < count;
        from = hasStart ? from : (forward ? -1 : count);

        for (var step = 1; step <= count; step++)
        {
            var row = forward
                ? (int)(((long)from + step) % count)
                : (int)((((long)from - step) % count + count) % count);
            if (hasStart && row == from)
            {
                continue; // wrapped all the way round: the starting row is not "the next one"
            }

            if (predicate(rows[row]))
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>A warning or worse; never a line that failed to parse.</summary>
    public static bool IsProblem(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Level is LogLevel.Warning or LogLevel.Error or LogLevel.Fatal;
    }
}

/// <summary>What the user asked for in the Go to box (FR-G7): an event number, or a point in time.</summary>
public abstract record GoToTarget
{
    /// <summary>1-based event number, as shown in the detail pane.</summary>
    public sealed record EventNumber(int Number) : GoToTarget;

    public sealed record Time(TimeLiteralValue Value) : GoToTarget;
}

public static class GoTo
{
    /// <summary>Parses <c>1234</c>, <c>#1234</c>, <c>2026-11-13 08:00:05</c> or <c>08:00:05</c> (date from <paramref name="referenceDate"/>).</summary>
    public static bool TryParse(string? input, DateTime? referenceDate, out GoToTarget? target, out string? error)
    {
        target = null;
        error = null;
        var text = input?.Trim().TrimStart('#').Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "Type an event number (e.g. 1200) or a time (e.g. 08:00:05 or 2026-11-13 08:00).";
            return false;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            if (number < 1)
            {
                error = "Event numbers start at 1.";
                return false;
            }

            target = new GoToTarget.EventNumber(number);
            return true;
        }

        if (TimeLiteral.TryParse(text, referenceDate, out var value))
        {
            if (!value.HasDate)
            {
                error = "Include the date, e.g. 2026-11-13 08:00:05.";
                return false;
            }

            target = new GoToTarget.Time(value);
            return true;
        }

        error = $"'{input?.Trim()}' isn't an event number or a time.";
        return false;
    }

    /// <summary>
    /// Index of the first event at or after <paramref name="target"/> in <paramref name="rows"/> (the last timed event when the target is later
    /// than all of them); -1 when no event has a timestamp. Unparsed lines are skipped.
    /// </summary>
    public static int IndexAtOrAfter(IReadOnlyList<LogEvent> rows, TimeLiteralValue target)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var lastTimed = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            if (e.IsUnparsed)
            {
                continue;
            }

            lastTimed = i;
            var reached = target.Instant is { } instant ? e.Timestamp >= instant : TimeRange.WallClock(e.Timestamp) >= target.Wall;
            if (reached)
            {
                return i;
            }
        }

        return lastTimed;
    }
}
