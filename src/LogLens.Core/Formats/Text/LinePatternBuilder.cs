using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.Text;

/// <summary>
/// Builds a <see cref="LinePattern"/> piece by piece, for the layout compilers of loggers whose tokens render plain values (NLog layouts, log4net
/// conversion patterns): literal text, timestamp fields, the level, the message, an exception on the same line, properties and optional sections.
/// </summary>
internal sealed class LinePatternBuilder
{
    /// <summary>Most properties one layout can name.</summary>
    public const int MaxProperties = 64;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2); // NFR-10: no catastrophic backtracking

    private readonly StringBuilder _pattern = new("^");
    private readonly List<TimestampField> _timestamp = [];
    private readonly List<(string Name, string Group)> _properties = [];
    private readonly string _kind;
    private bool _hasLevel;
    private bool _hasMessage;
    private bool _hasException;
    private bool _startsWithDigit;
    private bool _empty = true;
    private int _optionalDepth;
    private string? _lastLiteral;
    private int _lastLiteralEnd = -1;

    /// <param name="kind">What is being compiled, for messages, e.g. "NLog layout".</param>
    public LinePatternBuilder(string kind) => _kind = kind;

    /// <summary>True once a token that ends the first line (a new line) has been added: everything after it is ignored.</summary>
    public bool Stopped { get; private set; }

    public void Literal(string text)
    {
        if (Stopped || text.Length == 0)
        {
            return;
        }

        _lastLiteral = Regex.Escape(text);
        _pattern.Append(_lastLiteral);
        _lastLiteralEnd = _pattern.Length;
        _empty = false;
    }

    /// <param name="format">The .NET date/time format the timestamp is written with.</param>
    /// <exception cref="FormatException">The format can't be matched textually.</exception>
    public void Timestamp(string format, bool universal)
    {
        if (Stopped)
        {
            return;
        }

        string regex;
        try
        {
            regex = TimestampFormat.ToRegex(format);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The date format '{format}' isn't supported: {ex.Message}", ex);
        }

        var group = "ts" + _timestamp.Count.ToString(CultureInfo.InvariantCulture);
        if (_empty && _optionalDepth == 0)
        {
            _startsWithDigit = TimestampFormat.StartsWithDigit(format);
        }

        _pattern.Append("(?<").Append(group).Append('>').Append(regex).Append(')');
        _timestamp.Add(new TimestampField(group, format, universal));
        _empty = false;
    }

    /// <param name="pattern">What the level text looks like, e.g. <c>[A-Za-z]+</c>.</param>
    /// <param name="padding">Width the logger pads the level to: positive pads on the left, negative on the right (log4net <c>%-5level</c>).</param>
    public void Level(string pattern, int padding)
    {
        if (Stopped)
        {
            return;
        }

        _hasLevel = true;
        Padded("lvl", pattern, padding);
    }

    public void Message()
    {
        if (Stopped)
        {
            return;
        }

        if (_hasMessage)
        {
            throw new FormatException($"The message appears twice in the {_kind}.");
        }

        _hasMessage = true;
        _pattern.Append("(?<msg>.*?)");
        _empty = false;
    }

    /// <summary>
    /// An exception rendered on the event's own line: its type line, if one is there (an empty exception matches nothing). Stricter than a
    /// continuation line's header — a full type name followed by ':' or the line end, as <c>Exception.ToString()</c> writes it — so a message that
    /// merely mentions an exception ("Caught TimeoutException while …") isn't split.
    /// </summary>
    public void Exception()
    {
        if (Stopped || _hasException)
        {
            return;
        }

        _hasException = true;

        // The separator before an empty exception is still written ("message "), but may have been trimmed: it is optional.
        if (_lastLiteral is { } literal && _lastLiteralEnd == _pattern.Length)
        {
            _pattern.Length -= literal.Length;
            _pattern.Append("(?:").Append(literal).Append(")?");
        }

        _pattern.Append(SameLineException);
        _empty = false;
    }

    private const string SameLineException = @"(?<exc>(?:[A-Za-z_][\w`+<>]*\.)+(?:[A-Za-z_][\w`+<>]*)?(?:Exception|Error)(?::.*|$))?";

    /// <summary>A property: any text up to what follows it (it may be empty or padded).</summary>
    public void Property(string name)
    {
        if (Stopped)
        {
            return;
        }

        if (_properties.Count == MaxProperties)
        {
            throw new FormatException($"A {_kind} can name at most {MaxProperties} properties.");
        }

        var group = "p" + _properties.Count.ToString(CultureInfo.InvariantCulture);
        _pattern.Append("(?<").Append(group).Append(">.*?)");
        _properties.Add((name, group));
        _empty = false;
    }

    /// <summary>The rest of the layout is written on the following lines (a new line token).</summary>
    public void Stop() => Stopped = true;

    /// <summary>Starts a section that is only written sometimes (NLog <c>${onexception:…}</c>, <c>${when:…}</c>).</summary>
    public void BeginOptional()
    {
        if (!Stopped)
        {
            _pattern.Append("(?:");
            _optionalDepth++;
        }
    }

    public void EndOptional()
    {
        if (_optionalDepth > 0)
        {
            _pattern.Append(")?");
            _optionalDepth--;
        }
    }

    /// <exception cref="FormatException">The layout has no timestamp or no message.</exception>
    public LinePattern Build()
    {
        while (_optionalDepth > 0)
        {
            EndOptional();
        }

        if (_timestamp.Count == 0)
        {
            throw new FormatException($"The {_kind} must contain a date or time.");
        }

        if (!_hasMessage)
        {
            throw new FormatException($"The {_kind} must contain the message.");
        }

        _pattern.Append('$');
        var regex = new Regex(_pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.ExplicitCapture, MatchTimeout);
        return new LinePattern(regex, _timestamp, _hasLevel ? "lvl" : null, "msg", _hasException ? "exc" : null, _properties, _startsWithDigit);
    }

    /// <summary>A layout token name made into a property name: <c>threadid</c> → <c>Threadid</c>, <c>aspnet-request-url</c> → <c>AspnetRequestUrl</c>.</summary>
    public static string PropertyName(string token)
    {
        var sb = new StringBuilder(token.Length);
        var upper = true;
        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.Length == 0 ? "Value" : sb.ToString();
    }

    private void Padded(string group, string pattern, int padding)
    {
        if (padding > 0)
        {
            _pattern.Append(" *");
        }

        _pattern.Append("(?<").Append(group).Append('>').Append(pattern).Append(')');
        if (padding < 0)
        {
            _pattern.Append(" *");
        }

        _empty = false;
    }

    /// <summary>The name logging classes go by in every format (so the Context column and <c>SourceContext</c> queries work for all).</summary>
    public const string LoggerProperty = LogEvent.SourceContextProperty;
}
