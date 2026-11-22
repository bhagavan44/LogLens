using System.Text;
using LogLens.Core.Models;

namespace LogLens.Core.Analysis;

/// <summary>
/// Turns a rendered message into a stable "shape" so recurring events group together even though the default Serilog text template does not
/// keep the message template: numbers, GUIDs, hex ids and quoted values are replaced by placeholders
/// (<c>Order 42 shipped to 'alice'</c> → <c>Order {n} shipped to {text}</c>). Used by the "show / hide events like this one" quick filters (FR-S5) and, later, the statistics (FR-X1).
/// </summary>
/// <remarks>Hand-written single pass rather than a regex chain: it runs over every event of a large log.</remarks>
public static class MessageNormalizer
{
    private const int MaxQuotedLength = 200;

    /// <summary>The normalised form of the first line of <paramref name="message"/>.</summary>
    public static string Normalize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var line = LogText.FirstLine(message);
        if (line.Length == 0)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var atWordStart = i == 0 || !IsWord(line[i - 1]);

            if (atWordStart && IsWord(c))
            {
                i = AppendWord(line, i, sb);
            }
            else if (atWordStart && c is '\'' or '"' or '`' && TryFindClosingQuote(line, i, out var close))
            {
                sb.Append("{text}");
                i = close + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Handles the word starting at <paramref name="start"/>; returns the index just past what was consumed.</summary>
    private static int AppendWord(string line, int start, StringBuilder sb)
    {
        if (IsGuidAt(line, start))
        {
            sb.Append("{guid}");
            return start + 36;
        }

        var end = start;
        while (end < line.Length && IsWord(line[end]))
        {
            end++;
        }

        var digits = start;
        while (digits < end && IsDigit(line[digits]))
        {
            digits++;
        }

        // 0x1F4A
        if (end - start > 2 && line[start] == '0' && line[start + 1] is 'x' or 'X' && AllHex(line, start + 2, end))
        {
            sb.Append("{hex}");
            return end;
        }

        // 42, 3.14, 10.0.0.12, 08:00:05.123, 2026-11-13, 1,234
        if (digits == end)
        {
            return AppendNumber(line, start, sb);
        }

        // a long id such as 3f2504e04f8911d39a0c0305e82c3301 or deadbeef12
        if (end - start >= 8 && AllHex(line, start, end) && HasDigitAndLetter(line, start, end))
        {
            sb.Append("{hex}");
            return end;
        }

        // 250ms, 10MB, 5s: keep the unit, replace the number
        if (digits > start && end - digits <= 3 && AllLetters(line, digits, end))
        {
            sb.Append("{n}");
            sb.Append(line, digits, end - digits);
            return end;
        }

        sb.Append(line, start, end - start);
        return end;
    }

    private static int AppendNumber(string line, int start, StringBuilder sb)
    {
        var k = start;
        while (k < line.Length && IsDigit(line[k]))
        {
            k++;
        }

        // Absorb "." "," ":" "-" followed by more digits, so addresses, times, dates and versions stay one token.
        while (k + 1 < line.Length && line[k] is '.' or ',' or ':' or '-' && IsDigit(line[k + 1]))
        {
            k++;
            while (k < line.Length && IsDigit(line[k]))
            {
                k++;
            }
        }

        sb.Append("{n}");
        return k;
    }

    private static bool TryFindClosingQuote(string line, int open, out int close)
    {
        var quote = line[open];
        var last = Math.Min(line.Length - 1, open + MaxQuotedLength);
        for (var p = open + 1; p <= last; p++)
        {
            // A closing quote ends the word it follows; an apostrophe inside a word (it's) does not.
            if (line[p] == quote && (p + 1 == line.Length || !IsWord(line[p + 1])))
            {
                close = p;
                return true;
            }
        }

        close = -1;
        return false;
    }

    private static bool IsGuidAt(string s, int i)
    {
        if (s.Length - i < 36 || (s.Length - i > 36 && IsWord(s[i + 36])))
        {
            return false;
        }

        for (var k = 0; k < 36; k++)
        {
            var c = s[i + k];
            if (k is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                {
                    return false;
                }
            }
            else if (!IsHex(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool AllHex(string s, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (!IsHex(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllLetters(string s, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (!char.IsLetter(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasDigitAndLetter(string s, int from, int to)
    {
        bool digit = false, letter = false;
        for (var i = from; i < to; i++)
        {
            if (IsDigit(s[i]))
            {
                digit = true;
            }
            else
            {
                letter = true;
            }
        }

        return digit && letter;
    }
}
