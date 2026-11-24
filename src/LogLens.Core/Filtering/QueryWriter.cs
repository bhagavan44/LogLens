using System.Globalization;
using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>Writes query-bar text (FR-S4) for filters made with a click, e.g. "Filter to this value" in the properties table (FR-D5).</summary>
public static class QueryWriter
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "in", "like", "contains", "startswith", "endswith", "matches", "exists", "null", "true", "false",
    };

    /// <summary><c>Name = value</c> for property <paramref name="name"/>, or <c>not (Name = value)</c> when <paramref name="exclude"/> is set.</summary>
    public static string PropertyEquals(string name, LogValue? value, bool exclude = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        var clause = $"{PropertyName(name)} = {Literal(value)}";
        return exclude ? $"not ({clause})" : clause;
    }

    /// <summary>A property as the query bar reads it: the bare name when it is a plain word, otherwise <c>@p['name']</c>.</summary>
    public static string PropertyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var plain = name.Length > 0
                    && (char.IsLetter(name[0]) || name[0] == '_')
                    && name.All(c => char.IsLetterOrDigit(c) || c == '_')
                    && !Reserved.Contains(name);
        return plain ? name : $"@p[{Quote(name)}]";
    }

    /// <summary>A value as a query literal: a number, true / false, null, or text in quotes.</summary>
    public static string Literal(LogValue? value) => value?.Value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        // The query bar reads plain decimals only; "1E+20" goes in quotes, which still compares as a number with a numeric property.
        double d when double.IsFinite(d) && d.ToString("R", CultureInfo.InvariantCulture) is var r && !r.Contains('E') => r,
        _ => Quote(value.ToString()),
    };

    /// <summary>Text in single quotes, a quote inside doubled.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Adds <paramref name="clause"/> to <paramref name="query"/> with <c>and</c>; the existing query is put in parentheses when needed.</summary>
    public static string And(string? query, string clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        if (string.IsNullOrWhiteSpace(query))
        {
            return clause;
        }

        var trimmed = query.Trim();
        var needsParentheses = trimmed.Contains(" or ", StringComparison.OrdinalIgnoreCase);
        return needsParentheses ? $"({trimmed}) and {clause}" : $"{trimmed} and {clause}";
    }
}
