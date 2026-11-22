using System.Text.RegularExpressions;
using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>What the user typed into the find bar and its option toggles (FR-S3).</summary>
public sealed record SearchSpec(string Text, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false, bool IncludeException = true)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text);

    /// <summary>Compiles the spec. Returns null when the text is empty, or with <paramref name="error"/> set when the regular expression is invalid.</summary>
    public SearchMatcher? TryCompile(out string? error)
    {
        error = null;
        if (IsEmpty)
        {
            return null;
        }

        try
        {
            return new SearchMatcher(this);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message.Split('\n')[0].Trim();
            return null;
        }
    }
}

/// <summary>Matches events and finds the matching ranges inside a piece of text (for highlighting). Thread-safe.</summary>
public sealed class SearchMatcher
{
    private const int MaxHighlights = 64;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly Regex? _regex;
    private readonly string _text;
    private readonly StringComparison _comparison;
    private readonly bool _includeException;

    internal SearchMatcher(SearchSpec spec)
    {
        _text = spec.Text;
        _includeException = spec.IncludeException;
        _comparison = spec.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (spec.UseRegex || spec.WholeWord)
        {
            var pattern = spec.UseRegex ? spec.Text : Regex.Escape(spec.Text);
            if (spec.WholeWord)
            {
                pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
            }

            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (!spec.MatchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            _regex = new Regex(pattern, options, Timeout);
        }
    }

    public bool IsMatch(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Matches(e.Message) || (_includeException && e.Exception is { } exception && Matches(exception));
    }

    public bool Matches(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return _regex is null ? text.Contains(_text, _comparison) : _regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Non-empty ranges of <paramref name="text"/> that match (at most 64), for highlighting.</summary>
    public IReadOnlyList<(int Start, int Length)> FindRanges(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ranges = new List<(int, int)>();
        try
        {
            if (_regex is null)
            {
                var from = 0;
                while (ranges.Count < MaxHighlights && from <= text.Length - _text.Length)
                {
                    var at = text.IndexOf(_text, from, _comparison);
                    if (at < 0)
                    {
                        break;
                    }

                    ranges.Add((at, _text.Length));
                    from = at + Math.Max(1, _text.Length);
                }
            }
            else
            {
                foreach (Match m in _regex.Matches(text))
                {
                    if (m.Length > 0)
                    {
                        ranges.Add((m.Index, m.Length));
                        if (ranges.Count >= MaxHighlights)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // highlight nothing rather than hang
        }

        return ranges;
    }
}
