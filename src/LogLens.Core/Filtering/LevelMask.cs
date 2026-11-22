using LogLens.Core.Models;

namespace LogLens.Core.Filtering;

/// <summary>A set of <see cref="LogLevel"/>s packed into an int (one bit per level), used by the level filter (FR-S1).</summary>
public static class LevelMask
{
    /// <summary>Every level, including <see cref="LogLevel.Unknown"/> and <see cref="LogLevel.Unparsed"/>.</summary>
    public const int All = (1 << 8) - 1;

    public static int Bit(LogLevel level) => 1 << (int)level;

    public static bool Allows(int mask, LogLevel level) => (mask & Bit(level)) != 0;

    /// <summary>
    /// "Warning and above" style pick. Events without a severity (<see cref="LogLevel.Unknown"/>, <see cref="LogLevel.Unparsed"/>) are
    /// left out, unless <paramref name="minimum"/> is <see cref="LogLevel.Unknown"/>, which means "everything".
    /// </summary>
    public static int AtLeast(LogLevel minimum)
    {
        if (minimum == LogLevel.Unknown)
        {
            return All;
        }

        var mask = 0;
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            if (HasSeverity(level) && level >= minimum)
            {
                mask |= Bit(level);
            }
        }

        return mask;
    }

    public static int With(int mask, LogLevel level, bool on) => on ? mask | Bit(level) : mask & ~Bit(level);

    /// <summary>True for <see cref="LogLevel.Verbose"/> … <see cref="LogLevel.Fatal"/>, the levels that can be ordered by severity.</summary>
    public static bool HasSeverity(LogLevel level) => level is >= LogLevel.Verbose and <= LogLevel.Fatal;
}
