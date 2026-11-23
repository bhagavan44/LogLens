namespace LogLens.Core.Streaming;

/// <summary>A file in the log folder that might be the successor of the one being followed.</summary>
public readonly record struct RollCandidate(string Name, DateTime CreatedUtc);

/// <summary>
/// Recognises the files of a rolling log (<c>log20261113.txt</c> → <c>log20261114.txt</c>, <c>app-001.log</c> → <c>app-002.log</c>) so a
/// tail can follow the log to its next file (FR-T3).
/// </summary>
public static class RollPattern
{
    // A run of this many digits is taken to be a date stamp (yyyyMMdd, yyyyMMddHH …) rather than part of a name like "api2".
    private const int DateStampDigits = 6;

    /// <summary>
    /// A glob for sibling files: everything before the date stamp, then <c>*</c>, then the extension. The date stamp is the first run of at
    /// least six digits; if there is none, the first digit of any kind.
    /// <c>log20261113.txt</c> → <c>log*.txt</c>; <c>viewer-20261113_001.txt</c> → <c>viewer-*.txt</c>; <c>live2-20261113.txt</c> → <c>live2-*.txt</c>
    /// (so it doesn't swallow an unrelated <c>live20261113.txt</c>); <c>log-2026-11-13.txt</c> → <c>log-*.txt</c>; <c>app-001.log</c> → <c>app-*.log</c>.
    /// Null when the name has no digits (a log such as <c>app.log</c> that is rotated by renaming is handled by truncation detection instead).
    /// </summary>
    public static string? FromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];

        var start = FirstDigitRunOfAtLeast(stem, DateStampDigits);
        if (start < 0)
        {
            start = stem.AsSpan().IndexOfAnyInRange('0', '9');
        }

        return start < 0 ? null : string.Concat(stem.AsSpan(0, start), "*", extension);
    }

    private static int FirstDigitRunOfAtLeast(string text, int length)
    {
        var runStart = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isDigit = i < text.Length && text[i] is >= '0' and <= '9';
            if (isDigit)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }
            }
            else
            {
                if (runStart >= 0 && i - runStart >= length)
                {
                    return runStart;
                }

                runStart = -1;
            }
        }

        return -1;
    }

    /// <summary>Case-insensitive match supporting <c>*</c> (any run of characters) and <c>?</c> (one character).</summary>
    public static bool Matches(string pattern, string fileName) => Match(pattern, 0, fileName, 0);

    /// <summary>
    /// The file to switch to, or null to stay. A candidate must match the pattern and sort after the current name (date-stamped rolls
    /// sort ascending); the greatest such name wins. If none sorts later, a file created after the current one wins instead.
    /// </summary>
    public static string? PickSuccessor(string pattern, string currentName, DateTime currentCreatedUtc, IEnumerable<RollCandidate> candidates)
    {
        string? byName = null;
        RollCandidate? byAge = null;
        foreach (var c in candidates)
        {
            if (string.Equals(c.Name, currentName, StringComparison.OrdinalIgnoreCase) || !Matches(pattern, c.Name))
            {
                continue;
            }

            if (string.Compare(c.Name, currentName, StringComparison.OrdinalIgnoreCase) > 0)
            {
                if (byName is null || string.Compare(c.Name, byName, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    byName = c.Name;
                }
            }
            else if (c.CreatedUtc > currentCreatedUtc && (byAge is null || c.CreatedUtc > byAge.Value.CreatedUtc))
            {
                byAge = c;
            }
        }

        return byName ?? byAge?.Name;
    }

    private static bool Match(string pattern, int p, string text, int t)
    {
        while (p < pattern.Length)
        {
            var c = pattern[p];
            if (c == '*')
            {
                while (p < pattern.Length && pattern[p] == '*')
                {
                    p++;
                }

                if (p == pattern.Length)
                {
                    return true;
                }

                for (var i = t; i <= text.Length; i++)
                {
                    if (Match(pattern, p, text, i))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (t >= text.Length || (c != '?' && char.ToUpperInvariant(c) != char.ToUpperInvariant(text[t])))
            {
                return false;
            }

            p++;
            t++;
        }

        return t == text.Length;
    }
}
