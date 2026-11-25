using LogLens.Core.Models;

namespace LogLens.Core.Formats;

/// <summary>
/// Maps level text to <see cref="LogLevel"/> (FR-F4), ignoring case: Serilog's (<c>{Level:u3}</c>, <c>{Level:w3}</c>, <c>{Level:u1}</c>, full
/// names), NLog's (<c>Trace</c>, <c>Info</c>, <c>Warn</c>), log4net's (also <c>NOTICE</c>, <c>SEVERE</c>, <c>CRITICAL</c>, <c>ALERT</c>,
/// <c>EMERGENCY</c>, <c>FINE</c> …) and Microsoft.Extensions.Logging's (<c>trce</c>, <c>dbug</c>, <c>fail</c>, <c>crit</c>).
/// </summary>
public static class LogLevelParser
{
    public static LogLevel Parse(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        return text.Length switch
        {
            0 => LogLevel.Unknown,
            1 => ParseSingle(text[0]),
            3 => ParseThree(text),
            _ => ParseFull(text),
        };
    }

    private static LogLevel ParseSingle(char c) => char.ToUpperInvariant(c) switch
    {
        'V' or 'T' => LogLevel.Verbose,
        'D' => LogLevel.Debug,
        'I' => LogLevel.Information,
        'W' => LogLevel.Warning,
        'E' => LogLevel.Error,
        'F' => LogLevel.Fatal,
        _ => LogLevel.Unknown,
    };

    private static LogLevel ParseThree(ReadOnlySpan<char> text)
    {
        if (text.Equals("VRB", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Verbose;
        }

        if (text.Equals("DBG", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        if (text.Equals("INF", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Information;
        }

        if (text.Equals("WRN", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        if (text.Equals("ERR", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (text.Equals("FTL", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Fatal;
        }

        if (text.Equals("TRC", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Verbose;
        }

        return ParseFull(text);
    }

    private static LogLevel ParseFull(ReadOnlySpan<char> text)
    {
        if (text.Equals("Verbose", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Verbose;
        }

        if (text.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        if (text.Equals("Information", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Information;
        }

        if (text.Equals("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        if (text.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (text.Equals("Fatal", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Fatal;
        }

        return ParseSynonym(text);
    }

    /// <summary>Level names other loggers use.</summary>
    private static LogLevel ParseSynonym(ReadOnlySpan<char> text)
    {
        foreach (var (name, level) in Synonyms)
        {
            if (text.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return level;
            }
        }

        return LogLevel.Unknown;
    }

    private static readonly (string Name, LogLevel Level)[] Synonyms =
    [
        ("Info", LogLevel.Information),
        ("Warn", LogLevel.Warning),
        ("Trace", LogLevel.Verbose),
        ("trce", LogLevel.Verbose),
        ("Finest", LogLevel.Verbose),
        ("Finer", LogLevel.Verbose),
        ("Fine", LogLevel.Debug),
        ("dbug", LogLevel.Debug),
        ("Notice", LogLevel.Information),
        ("fail", LogLevel.Error),
        ("Severe", LogLevel.Error),
        ("Critical", LogLevel.Fatal),
        ("crit", LogLevel.Fatal),
        ("Alert", LogLevel.Fatal),
        ("Emergency", LogLevel.Fatal),
    ];
}
