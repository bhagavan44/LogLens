using LogLens.Core.Models;

namespace LogLens.Core.Formats;

/// <summary>Maps Serilog level text (<c>{Level:u3}</c>, <c>{Level:w3}</c>, <c>{Level:u1}</c>, full names) to <see cref="LogLevel"/>.</summary>
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
        'V' => LogLevel.Verbose,
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

        return LogLevel.Unknown;
    }
}
