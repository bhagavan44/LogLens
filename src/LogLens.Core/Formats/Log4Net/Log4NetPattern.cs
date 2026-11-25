using System.Globalization;
using System.Text;
using LogLens.Core.Formats.Text;

namespace LogLens.Core.Formats.Log4Net;

/// <summary>
/// Compiles a log4net <c>PatternLayout</c> conversion pattern (<c>%date [%thread] %-5level %logger - %message%newline</c>) into the
/// <see cref="LinePattern"/> of the first line of an event. Lines that don't match are continuation lines of the previous event (FR-F3); that is
/// also where log4net writes an event's exception.
/// </summary>
/// <remarks>
/// <para>Each conversion is <c>%[-][min][.max]name[{option}]</c>; <c>%%</c> is a percent sign. <c>%date</c> / <c>%d</c> (<c>{ISO8601}</c>,
/// <c>{ABSOLUTE}</c>, <c>{DATE}</c> or a .NET format) and <c>%utcdate</c> are the timestamp, <c>%level</c> / <c>%p</c> the level,
/// <c>%message</c> / <c>%m</c> the message and <c>%logger</c> / <c>%c</c> the <c>SourceContext</c> property; <c>%exception</c> on the event's
/// line becomes the exception and <c>%newline</c> / <c>%n</c> ends the first line.</para>
/// <para>Every other conversion becomes a property: <c>%thread</c> is <c>Thread</c>, <c>%property{RequestId}</c> (also <c>%P</c>, <c>%X</c>,
/// <c>%mdc</c>) is <c>RequestId</c>, unknown conversions are named after themselves.</para>
/// </remarks>
public static class Log4NetPattern
{
    /// <summary>The conversion pattern of log4net's documentation and most configurations.</summary>
    public const string DefaultText = "%date [%thread] %-5level %logger - %message%newline";

    private const string Kind = "log4net pattern";

    /// <summary>log4net's ISO 8601 date format (<c>%date</c> without an option).</summary>
    private const string Iso8601 = "yyyy-MM-dd HH:mm:ss,fff";

    /// <summary>One-letter conversions; case matters (<c>%c</c> is the logger, <c>%C</c> the class).</summary>
    private static readonly Dictionary<char, string> Letters = new()
    {
        ['a'] = "appdomain",
        ['c'] = "logger",
        ['C'] = "class",
        ['d'] = "date",
        ['F'] = "file",
        ['l'] = "location",
        ['L'] = "line",
        ['m'] = "message",
        ['M'] = "method",
        ['n'] = "newline",
        ['p'] = "level",
        ['P'] = "property",
        ['r'] = "timestamp",
        ['t'] = "thread",
        ['u'] = "identity",
        ['w'] = "username",
        ['x'] = "ndc",
        ['X'] = "property",
    };

    private static readonly Dictionary<string, string> PropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appdomain"] = "AppDomain",
        ["class"] = "Class",
        ["type"] = "Class",
        ["file"] = "File",
        ["location"] = "Location",
        ["line"] = "Line",
        ["method"] = "Method",
        ["timestamp"] = "Elapsed",
        ["thread"] = "Thread",
        ["identity"] = "Identity",
        ["username"] = "UserName",
        ["ndc"] = "NDC",
        ["stacktrace"] = "StackTrace",
        ["stacktracedetail"] = "StackTraceDetail",
    };

    /// <summary>Every long conversion name, longest first, so the longest one that fits is taken (<c>%utcdate</c> before <c>%u</c>).</summary>
    private static readonly string[] Names =
    [
        .. new[]
        {
            "appdomain", "aspnet-cache", "aspnet-context", "aspnet-request", "aspnet-session", "class", "date", "exception", "file", "identity",
            "level", "line", "location", "logger", "mdc", "message", "method", "newline", "ndc", "properties", "property", "stacktracedetail",
            "stacktrace", "thread", "timestamp", "type", "username", "utcdate",
        }.OrderByDescending(n => n.Length),
    ];

    /// <summary>The default pattern, compiled.</summary>
    public static LinePattern Default { get; } = Parse(DefaultText);

    /// <exception cref="FormatException">The pattern is malformed or lacks a date or the message.</exception>
    public static LinePattern Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var builder = new LinePatternBuilder(Kind);
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length && !builder.Stopped)
        {
            var c = pattern[i];
            if (c != '%')
            {
                literal.Append(c);
                i++;
                continue;
            }

            if (i + 1 < pattern.Length && pattern[i + 1] == '%')
            {
                literal.Append('%');
                i += 2;
                continue;
            }

            builder.Literal(literal.ToString());
            literal.Clear();
            var start = i;
            i++;
            var padding = ReadPadding(pattern, ref i);
            var name = ReadName(pattern, ref i) ?? throw new FormatException($"'{pattern[start..Math.Min(pattern.Length, i + 1)]}' isn't a conversion; write %% for a percent sign.");
            var option = ReadOption(pattern, ref i);
            Compile(name, option, padding, builder);
        }

        builder.Literal(literal.ToString());
        return builder.Build();
    }

    private static void Compile(string name, string? option, int padding, LinePatternBuilder builder)
    {
        switch (name.ToLowerInvariant())
        {
            case "date":
                builder.Timestamp(DateFormat(option), universal: false);
                break;
            case "utcdate":
                builder.Timestamp(DateFormat(option), universal: true);
                break;
            case "level":
                builder.Level("[A-Za-z]+", padding);
                break;
            case "message":
                builder.Message();
                break;
            case "logger":
                builder.Property(LinePatternBuilder.LoggerProperty);
                break;
            case "exception":
                builder.Exception();
                break;
            case "newline":
                builder.Stop();
                break;
            case "property" or "properties" or "mdc":
                builder.Property(string.IsNullOrWhiteSpace(option) ? "Properties" : option.Trim());
                break;
            default:
                builder.Property(PropertyNames.TryGetValue(name, out var known) ? known : LinePatternBuilder.PropertyName(name));
                break;
        }
    }

    /// <summary>The .NET format of a date conversion's option: log4net's named formats, or the option itself.</summary>
    private static string DateFormat(string? option) => option?.Trim() switch
    {
        null or "" => Iso8601,
        var o when o.Equals("ISO8601", StringComparison.OrdinalIgnoreCase) => Iso8601,
        var o when o.Equals("ABSOLUTE", StringComparison.OrdinalIgnoreCase) => "HH:mm:ss,fff",
        var o when o.Equals("DATE", StringComparison.OrdinalIgnoreCase) => "dd MMM yyyy HH:mm:ss,fff",
        var o => o,
    };

    /// <summary>The <c>[-][min][.max]</c> of a conversion: the minimum width, negative when the text is padded on the right.</summary>
    private static int ReadPadding(string pattern, ref int i)
    {
        var left = true;
        if (i < pattern.Length && pattern[i] == '-')
        {
            left = false;
            i++;
        }

        var start = i;
        while (i < pattern.Length && char.IsAsciiDigit(pattern[i]))
        {
            i++;
        }

        var min = i > start ? int.Parse(pattern.AsSpan(start, i - start), CultureInfo.InvariantCulture) : 0;
        if (i < pattern.Length && pattern[i] == '.')
        {
            i++;
            while (i < pattern.Length && char.IsAsciiDigit(pattern[i]))
            {
                i++; // the maximum width truncates, which reading doesn't need to know
            }
        }

        return left ? min : -min;
    }

    /// <summary>The conversion name: the longest known name that fits, a known letter, or an unknown word; null when there is none.</summary>
    private static string? ReadName(string pattern, ref int i)
    {
        foreach (var known in Names)
        {
            if (string.Compare(pattern, i, known, 0, known.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                i += known.Length;
                return known;
            }
        }

        var start = i;
        while (i < pattern.Length && (char.IsAsciiLetter(pattern[i]) || (i > start && pattern[i] == '-')))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        if (i - start == 1 && Letters.TryGetValue(pattern[start], out var letter))
        {
            return letter;
        }

        // A letter conversion followed by text (%dZ is not a conversion called "dZ"), else an unknown conversion named by its word.
        if (Letters.TryGetValue(pattern[start], out var first) && !Names.Contains(pattern[start..i], StringComparer.OrdinalIgnoreCase))
        {
            i = start + 1;
            return first;
        }

        return pattern[start..i];
    }

    private static string? ReadOption(string pattern, ref int i)
    {
        if (i >= pattern.Length || pattern[i] != '{')
        {
            return null;
        }

        var close = pattern.IndexOf('}', i + 1);
        if (close < 0)
        {
            throw new FormatException($"'{pattern[i..]}' has no closing '}}'.");
        }

        var option = pattern[(i + 1)..close];
        i = close + 1;
        return option;
    }
}
