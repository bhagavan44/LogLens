using System.Text.RegularExpressions;

namespace LogLens.Core.Formats.Text;

/// <summary>
/// Turns a user's .NET regular expression for the first line of an event into a <see cref="LinePattern"/> (SPEC §2 F5, custom regex). Lines that
/// don't match are continuation lines of the previous event (FR-F3).
/// </summary>
/// <remarks>
/// Named groups say what the line holds: <c>timestamp</c> (or <c>date</c> and <c>time</c>, read together) and <c>message</c> are required;
/// <c>level</c> and <c>exception</c> are optional; <c>logger</c> is the <c>SourceContext</c> property; any other named group is a property of that
/// name. The timestamp may be any common date and time text (<see cref="TimestampFormat.TryParseAny"/>). The expression is anchored at the start
/// of the line and matched with a timeout (NFR-10).
/// </remarks>
public static class RegexLinePattern
{
    /// <summary>An example for the template editor.</summary>
    public const string Example = @"^\[(?<timestamp>[^\]]+)\] (?<level>\w+)\s+(?<logger>\S+) :: (?<message>.*)$";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <exception cref="FormatException">The expression is invalid or lacks a timestamp or message group.</exception>
    public static LinePattern Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Regex regex;
        try
        {
            regex = new Regex("^(?:" + expression + ")", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Not a valid regular expression: {ex.Message}", ex);
        }

        var names = regex.GetGroupNames().Where(n => !int.TryParse(n, out _)).ToList();
        string? Find(string name) => names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

        List<TimestampField> timestamp = Find("timestamp") is { } ts
            ? [new TimestampField(ts, null)]
            : [.. new[] { Find("date"), Find("time") }.OfType<string>().Select(g => new TimestampField(g, null))];
        if (timestamp.Count == 0)
        {
            throw new FormatException("The expression needs a (?<timestamp>…) group (or (?<date>…) and (?<time>…)).");
        }

        var message = Find("message") ?? throw new FormatException("The expression needs a (?<message>…) group.");
        var level = Find("level");
        var exception = Find("exception");
        var special = new HashSet<string?>(timestamp.Select(t => t.Group).Append(message).Append(level).Append(exception), StringComparer.Ordinal);
        var properties = names
            .Where(n => !special.Contains(n))
            .Take(LinePatternBuilder.MaxProperties)
            .Select(n => (n.Equals("logger", StringComparison.OrdinalIgnoreCase) ? LinePatternBuilder.LoggerProperty : n, n))
            .ToList();

        return new LinePattern(regex, timestamp, level, message, exception, properties, startsWithDigit: false);
    }
}
