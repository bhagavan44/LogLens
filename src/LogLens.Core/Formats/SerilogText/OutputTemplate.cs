using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LogLens.Core.Formats.SerilogText;

/// <summary>
/// A Serilog <em>output template</em> (e.g. the file sink default) compiled into a regular expression that recognises the
/// first line of an event. Lines that do not match are continuation lines of the previous event (FR-F2, FR-F3).
/// </summary>
/// <remarks>
/// Supported tokens: <c>{Timestamp[:format]}</c>, <c>{Level[:u3|w3|t3|u1|w1|t1|u|w|t]}</c>, <c>{Message[:lj]}</c>,
/// <c>{SourceContext}</c>, <c>{NewLine}</c> and <c>{Exception}</c>; <c>{{</c>/<c>}}</c> escape braces. Anything after
/// <c>{NewLine}</c> describes the following (exception) lines and is not matched. Other tokens throw <see cref="FormatException"/>.
/// </remarks>
public sealed class OutputTemplate
{
    /// <summary>Serilog's default file sink template.</summary>
    public const string DefaultText = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2); // NFR-10: no catastrophic backtracking

    private OutputTemplate(string text, Regex startOfEvent, string timestampFormat, bool startsWithDigit, bool hasSourceContext)
    {
        Text = text;
        StartOfEvent = startOfEvent;
        TimestampFormat = timestampFormat;
        StartsWithDigit = startsWithDigit;
        HasSourceContext = hasSourceContext;
        ParseTimestamp = SerilogText.TimestampFormat.CreateParser(timestampFormat);
    }

    public static OutputTemplate Default { get; } = Parse(DefaultText);

    public string Text { get; }

    /// <summary>Matches the first line of an event. Named groups: <c>ts</c>, <c>lvl</c>, <c>msg</c> and optionally <c>ctx</c>.</summary>
    public Regex StartOfEvent { get; }

    public string TimestampFormat { get; }

    /// <summary>True when an event line must begin with a digit, which lets the parser reject most continuation lines without running the regex.</summary>
    public bool StartsWithDigit { get; }

    public bool HasSourceContext { get; }

    internal SerilogText.TimestampFormat.TimestampParser ParseTimestamp { get; }

    /// <exception cref="FormatException">The template is malformed, lacks Timestamp/Level/Message, or uses an unsupported token.</exception>
    public static OutputTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var pattern = new StringBuilder("^");
        var literal = new StringBuilder();
        string? timestampFormat = null;
        var hasLevel = false;
        var hasMessage = false;
        var hasContext = false;
        var startsWithDigit = false;
        var firstToken = true;
        var stop = false;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                if (firstToken)
                {
                    firstToken = false;
                }

                pattern.Append(Regex.Escape(literal.ToString()));
                literal.Clear();
            }
        }

        for (var i = 0; i < template.Length && !stop; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormatException("Unterminated '{' in output template.");
                }

                var inner = template.Substring(i + 1, close - i - 1);
                i = close;
                SplitToken(inner, out var name, out var format);
                FlushLiteral();

                switch (name)
                {
                    case "Timestamp":
                        timestampFormat = format ?? "O";
                        pattern.Append("(?<ts>").Append(SerilogText.TimestampFormat.ToRegex(timestampFormat)).Append(')');
                        if (firstToken)
                        {
                            startsWithDigit = StartsWithNumericField(timestampFormat);
                        }

                        break;
                    case "Level":
                        hasLevel = true;
                        pattern.Append("(?<lvl>").Append(LevelPattern(format)).Append(')');
                        break;
                    case "Message":
                        hasMessage = true;
                        pattern.Append("(?<msg>.*)");
                        break;
                    case "SourceContext":
                        hasContext = true;
                        pattern.Append("(?<ctx>[^\\s]+?)");
                        break;
                    case "NewLine" or "Exception":
                        // Everything from here on lives on the following lines.
                        stop = true;
                        break;
                    default:
                        throw new FormatException($"Output template token '{{{name}}}' isn't supported yet.");
                }

                firstToken = false;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i++;
                    continue;
                }

                throw new FormatException("Unmatched '}' in output template.");
            }
            else
            {
                literal.Append(c);
            }
        }

        if (!stop)
        {
            FlushLiteral();
        }

        if (timestampFormat is null)
        {
            throw new FormatException("The output template must contain {Timestamp}.");
        }

        if (!hasLevel)
        {
            throw new FormatException("The output template must contain {Level}.");
        }

        if (!hasMessage)
        {
            throw new FormatException("The output template must contain {Message}.");
        }

        pattern.Append('$');
        var regex = new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.ExplicitCapture, MatchTimeout);
        return new OutputTemplate(template, regex, timestampFormat, startsWithDigit, hasContext);
    }

    private static void SplitToken(string inner, out string name, out string? format)
    {
        var colon = inner.IndexOf(':');
        var head = colon >= 0 ? inner[..colon] : inner;
        format = colon >= 0 ? inner[(colon + 1)..] : null;

        var comma = head.IndexOf(',');
        name = (comma >= 0 ? head[..comma] : head).Trim();
    }

    private static string LevelPattern(string? format)
    {
        if (format is null || format.Length == 0)
        {
            return "[A-Za-z]+";
        }

        // u3 / w3 / t3 / u1 ... or u / w / t (full name)
        var width = format.Length > 1 && int.TryParse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return width > 0 ? $"[A-Za-z]{{{width}}}" : "[A-Za-z]+";
    }

    private static bool StartsWithNumericField(string format)
        => format is "o" or "O" or "s" || (format.Length > 0 && "yMdHhms".Contains(format[0]) && !(format[0] == 'M' && RunStartsWith(format, 'M') >= 3));

    private static int RunStartsWith(string format, char c)
    {
        var n = 0;
        while (n < format.Length && format[n] == c)
        {
            n++;
        }

        return n;
    }
}
