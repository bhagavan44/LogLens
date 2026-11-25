using System.Globalization;
using System.Text;

namespace LogLens.Core.Formats.Text;

/// <summary>
/// Turns a .NET date/time format string (as used in <c>{Timestamp:format}</c>) into a regular-expression fragment
/// that matches text written with it, and into a parser for that text.
/// </summary>
public static class TimestampFormat
{
    /// <summary>The format of Serilog's default file template.</summary>
    public const string Default = "yyyy-MM-dd HH:mm:ss.fff zzz";

    /// <summary>Builds the regex fragment (no anchors, no capture group) for <paramref name="format"/>.</summary>
    /// <exception cref="FormatException">The format uses specifiers that can't be matched textually (e.g. month names in other cultures).</exception>
    public static string ToRegex(string format)
    {
        if (format is "o" or "O")
        {
            return @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})";
        }

        if (format is "s")
        {
            return @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            var run = RunLength(format, i);
            switch (c)
            {
                case 'y':
                    sb.Append(run >= 3 ? @"\d{4}" : run == 2 ? @"\d{2}" : @"\d{1,4}");
                    break;
                case 'M':
                    sb.Append(run switch { 1 => @"\d{1,2}", 2 => @"\d{2}", 3 => "[A-Za-z]{3}", _ => "[A-Za-z]+" });
                    break;
                case 'd':
                    sb.Append(run switch { 1 => @"\d{1,2}", 2 => @"\d{2}", 3 => "[A-Za-z]{3}", _ => "[A-Za-z]+" });
                    break;
                case 'H' or 'h' or 'm' or 's':
                    sb.Append(run == 1 ? @"\d{1,2}" : @"\d{2}");
                    break;
                case 'f':
                    sb.Append(@"\d{").Append(Math.Min(run, 7)).Append('}');
                    break;
                case 'F':
                    sb.Append(@"(?:\.?\d{1,").Append(Math.Min(run, 7)).Append("})?");
                    break;
                case 'z':
                    sb.Append(run switch { 1 => @"[+-]\d{1,2}", 2 => @"[+-]\d{2}", _ => @"[+-]\d{2}:\d{2}" });
                    break;
                case 'K':
                    sb.Append(@"(?:Z|[+-]\d{2}:\d{2})?");
                    break;
                case 't':
                    sb.Append(run == 1 ? "[AaPp]" : "[AaPp][Mm]");
                    break;
                case '\'' or '"':
                    var close = format.IndexOf(c, i + 1);
                    if (close < 0)
                    {
                        throw new FormatException($"Unterminated quote in timestamp format '{format}'.");
                    }

                    sb.Append(System.Text.RegularExpressions.Regex.Escape(format.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                case '\\' when i + 1 < format.Length:
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(format[i + 1].ToString()));
                    i += 2;
                    continue;
                case '/':
                    sb.Append('/');
                    break;
                case ':':
                    sb.Append(':');
                    break;
                case 'g' or 'Z':
                    throw new FormatException($"Timestamp format '{format}' uses '{c}', which isn't supported.");
                default:
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                    run = 1;
                    break;
            }

            i += run;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates a parser for text written with <paramref name="format"/>. Text without an offset is local time, or UTC when
    /// <paramref name="universal"/>. The default format takes a hand-written fast path (about 10× quicker than <c>TryParseExact</c>).
    /// </summary>
    public static TimestampParser CreateParser(string format, bool universal = false)
    {
        if (format == Default)
        {
            return ParseDefault;
        }

        var styles = universal ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
        return (ReadOnlySpan<char> text, out DateTimeOffset value) =>
            DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, styles, out value);
    }

    public delegate bool TimestampParser(ReadOnlySpan<char> text, out DateTimeOffset value);

    /// <summary>
    /// Reads date and time text of no known format (a custom regular expression's timestamp group): ISO 8601 with or without <c>T</c> and offset,
    /// <c>2026-11-13 08:00:05,301</c> (a comma before the fraction, as log4net writes), <c>13 Nov 2026 08:00:05</c>, <c>11/13/2026 08:00</c> … Text
    /// without an offset is local time.
    /// </summary>
    public static bool TryParseAny(ReadOnlySpan<char> text, out DateTimeOffset value) => TryParseAny(text, DateTimeStyles.AssumeLocal, out value);

    /// <summary>As <see cref="TryParseAny(ReadOnlySpan{char}, out DateTimeOffset)"/>, but text without an offset is UTC.</summary>
    public static bool TryParseAnyUniversal(ReadOnlySpan<char> text, out DateTimeOffset value) => TryParseAny(text, DateTimeStyles.AssumeUniversal, out value);

    private static bool TryParseAny(ReadOnlySpan<char> text, DateTimeStyles styles, out DateTimeOffset value)
    {
        value = default;
        text = text.Trim();
        if (text.Length is 0 or > 64)
        {
            return false;
        }

        // "08:00:05,301" -> "08:00:05.301": a comma between the seconds and their fraction.
        Span<char> buffer = stackalloc char[text.Length];
        text.CopyTo(buffer);
        for (var i = 3; i < buffer.Length - 1; i++)
        {
            if (buffer[i] == ',' && buffer[i - 3] == ':' && char.IsAsciiDigit(buffer[i - 1]) && char.IsAsciiDigit(buffer[i + 1]))
            {
                buffer[i] = '.';
            }
        }

        return DateTimeOffset.TryParse(buffer, CultureInfo.InvariantCulture, styles | DateTimeStyles.AllowWhiteSpaces, out value);
    }

    private static bool ParseDefault(ReadOnlySpan<char> s, out DateTimeOffset value)
    {
        value = default;

        // "yyyy-MM-dd HH:mm:ss.fff zzz" is a fixed 30-character layout, e.g. "2026-11-13 08:00:00.012 +05:30".
        if (s.Length != 30 || s[4] != '-' || s[7] != '-' || s[10] != ' ' || s[13] != ':' || s[16] != ':'
            || s[19] != '.' || s[23] != ' ' || s[27] != ':' || (s[24] != '+' && s[24] != '-'))
        {
            return false;
        }

        if (!TryDigits(s, 0, 4, out var year) || !TryDigits(s, 5, 2, out var month) || !TryDigits(s, 8, 2, out var day)
            || !TryDigits(s, 11, 2, out var hour) || !TryDigits(s, 14, 2, out var minute) || !TryDigits(s, 17, 2, out var second)
            || !TryDigits(s, 20, 3, out var millisecond) || !TryDigits(s, 25, 2, out var offsetHours) || !TryDigits(s, 28, 2, out var offsetMinutes))
        {
            return false;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59 || offsetHours > 14 || offsetMinutes > 59)
        {
            return false;
        }

        try
        {
            var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
            if (s[24] == '-')
            {
                offset = offset.Negate();
            }

            value = new DateTimeOffset(year, month, day, hour, minute, second, millisecond, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // e.g. 31 February
        }
    }

    private static bool TryDigits(ReadOnlySpan<char> s, int start, int count, out int result)
    {
        result = 0;
        for (var i = 0; i < count; i++)
        {
            var d = s[start + i] - '0';
            if ((uint)d > 9)
            {
                return false;
            }

            result = (result * 10) + d;
        }

        return true;
    }

    /// <summary>True when text written with <paramref name="format"/> always begins with a digit (no day or month name, no literal first).</summary>
    public static bool StartsWithDigit(string format)
        => format is "o" or "O" or "s"
           || (format.Length > 0 && "yMdHhmsf".Contains(format[0]) && !(format[0] is 'M' or 'd' && RunLength(format, 0) >= 3));

    private static int RunLength(string format, int index)
    {
        var c = format[index];
        var n = 1;
        while (index + n < format.Length && format[index + n] == c)
        {
            n++;
        }

        return n;
    }
}
