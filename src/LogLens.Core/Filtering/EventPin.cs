using LogLens.Core.Analysis;
using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>
/// A group of events with the same message shape (see <see cref="MessageNormalizer"/>), picked from a row's context menu: "show only events
/// like this one" or "hide events like this one" (FR-S5). Shown as a removable chip next to the other filters.
/// </summary>
public sealed record EventPin(string Label, Func<LogEvent, bool> Predicate)
{
    /// <summary>Events whose message normalises to <paramref name="key"/>.</summary>
    public static EventPin ForMessage(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key, e => !e.IsUnparsed && MessageNormalizer.Normalize(e.Message) == key);
    }

    /// <summary>The group <paramref name="e"/> belongs to, or null for an unparsed line (it has no message to group by).</summary>
    public static EventPin? Like(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsUnparsed ? null : ForMessage(MessageNormalizer.Normalize(e.Message));
    }
}

/// <summary>Expands a set of matches with the events around them, like <c>grep -C</c> ("only matches" with context, FR-S3).</summary>
public static class ContextExpander
{
    /// <summary>
    /// Indices of every event that is a match or lies within <paramref name="context"/> events of one, counting only events accepted by
    /// <paramref name="visible"/> (so "2 events of context" means two <em>shown</em> events, not two hidden by other filters).
    /// </summary>
    public static int[] Compute(
        IReadOnlyList<LogEvent> events,
        Func<LogEvent, bool>? visible,
        Func<LogEvent, bool> isMatch,
        int context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(isMatch);
        context = Math.Max(0, context);

        var count = events.Count;
        var shown = new List<int>(Math.Min(count, 4096));
        var matched = new List<bool>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var e = events[i];
            if (visible is null || visible(e))
            {
                shown.Add(i);
                matched.Add(isMatch(e));
            }
        }

        var keep = new bool[shown.Count];
        var lastMatch = int.MinValue;
        for (var p = 0; p < shown.Count; p++)
        {
            if (matched[p])
            {
                lastMatch = p;
            }

            if (lastMatch != int.MinValue && p - lastMatch <= context)
            {
                keep[p] = true;
            }
        }

        var nextMatch = int.MaxValue;
        for (var p = shown.Count - 1; p >= 0; p--)
        {
            if (matched[p])
            {
                nextMatch = p;
            }

            if (nextMatch != int.MaxValue && nextMatch - p <= context)
            {
                keep[p] = true;
            }
        }

        var result = new List<int>();
        for (var p = 0; p < shown.Count; p++)
        {
            if (keep[p])
            {
                result.Add(shown[p]);
            }
        }

        return [.. result];
    }
}
