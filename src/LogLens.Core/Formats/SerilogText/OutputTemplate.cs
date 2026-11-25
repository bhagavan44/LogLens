using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LogLens.Core.Formats.Text;

namespace LogLens.Core.Formats.SerilogText;

/// <summary>
/// A Serilog <em>output template</em> (e.g. the file sink default) compiled into a regular expression that recognises the
/// first line of an event. Lines that do not match are continuation lines of the previous event (FR-F2, FR-F3).
/// </summary>
/// <remarks>
/// Supported tokens: <c>{Timestamp[:format]}</c>, <c>{Level[:u3|w3|t3|u1|w1|t1|u|w|t]}</c>, <c>{Message[:lj]}</c>, <c>{NewLine}</c>,
/// <c>{Exception}</c> and any property (<c>{SourceContext}</c>, <c>{ThreadId}</c>, <c>{RequestId}</c> …), whose value becomes a property of
/// the event; <c>{{</c>/<c>}}</c> escape braces. Anything after <c>{NewLine}</c> describes the following (exception) lines and is not matched.
/// <c>{Properties}</c> and malformed templates throw <see cref="FormatException"/>.
/// </remarks>
public sealed class OutputTemplate
{
    /// <summary>Serilog's default file sink template.</summary>
    public const string DefaultText = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Most properties one template can name.</summary>
    public const int MaxProperties = 64;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2); // NFR-10: no catastrophic backtracking

    private OutputTemplate(string text, Regex startOfEvent, string timestampFormat, bool startsWithDigit, IReadOnlyList<string> propertyNames)
    {
        Text = text;
        TimestampFormat = timestampFormat;
        Pattern = new LinePattern(
            startOfEvent,
            [new TimestampField("ts", timestampFormat)],
            levelGroup: "lvl",
            messageGroup: "msg",
            exceptionGroup: null,
            [.. propertyNames.Select((name, i) => (name, "p" + i.ToString(CultureInfo.InvariantCulture)))],
            startsWithDigit);
    }

    public static OutputTemplate Default { get; } = Parse(DefaultText);

    public string Text { get; }

    /// <summary>The event lines the template writes, as the plain-text parser reads them.</summary>
    public LinePattern Pattern { get; }

    /// <summary>Matches the first line of an event. Named groups: <c>ts</c>, <c>lvl</c>, <c>msg</c>, and <c>p0</c>, <c>p1</c> … for <see cref="PropertyNames"/>.</summary>
    public Regex StartOfEvent => Pattern.StartOfEvent;

    public string TimestampFormat { get; }

    /// <summary>True when an event line must begin with a digit, which lets the parser reject most continuation lines without running the regex.</summary>
    public bool StartsWithDigit => Pattern.StartsWithDigit;

    /// <summary>The properties the template writes on an event's first line, in order; group <c>p<em>i</em></c> captures the <em>i</em>-th.</summary>
    public IReadOnlyList<string> PropertyNames => Pattern.PropertyNames;

    public bool HasSourceContext => Pattern.HasSourceContext;

    /// <exception cref="FormatException">The template is malformed, lacks Timestamp/Level/Message, or uses an unsupported token.</exception>
    public static OutputTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var pattern = new StringBuilder("^");
        var literal = new StringBuilder();
        string? timestampFormat = null;
        var hasLevel = false;
        var hasMessage = false;
        var properties = new List<string>();
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
                        pattern.Append("(?<ts>").Append(Formats.Text.TimestampFormat.ToRegex(timestampFormat)).Append(')');
                        if (firstToken)
                        {
                            startsWithDigit = Formats.Text.TimestampFormat.StartsWithDigit(timestampFormat);
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
                    case "NewLine" or "Exception":
                        // Everything from here on lives on the following lines.
                        stop = true;
                        break;
                    case "Properties":
                        throw new FormatException("The {Properties} token isn't supported; name the properties instead, e.g. {RequestId}.");
                    default:
                        if (name.Length == 0 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                        {
                            throw new FormatException($"'{{{inner}}}' isn't a valid output template token.");
                        }

                        if (properties.Count == MaxProperties)
                        {
                            throw new FormatException($"An output template can name at most {MaxProperties} properties.");
                        }

                        if (properties.Contains(name))
                        {
                            throw new FormatException($"{{{name}}} appears twice in the output template.");
                        }

                        // Text in double quotes (Serilog quotes string values), or a run without spaces (possibly empty).
                        pattern.Append("(?<p").Append(properties.Count.ToString(CultureInfo.InvariantCulture)).Append(@">""(?:[^""\\]|\\.)*""|\S*?)");
                        properties.Add(name);
                        break;
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
        return new OutputTemplate(template, regex, timestampFormat, startsWithDigit, properties);
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
}
