using System.Globalization;

namespace LogLens.Core.Models;

/// <summary>What the grid's Time column shows (FR-G5).</summary>
public enum TimeColumnMode
{
    /// <summary>The event's timestamp.</summary>
    Timestamp,

    /// <summary>Time since the event before it in time (the row above, or below in a newest-first list).</summary>
    SincePrevious,

    /// <summary>Time since (or before) the selected event.</summary>
    SinceSelected,
}

/// <summary>
/// How an event is rendered as text in the grid, the detail pane and the clipboard. Kept in Core so it is unit-tested; the WinUI layer only
/// adds colours. Timestamps are shown as logged (zone and format options arrive with the settings round).
/// </summary>
public static class LogText
{
    /// <summary>Format of the Time column.</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Format of the full timestamp in the detail pane.</summary>
    public const string FullTimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static string Time(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsUnparsed ? "—" : e.Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    public static string FullTime(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsUnparsed ? "no timestamp" : e.Timestamp.ToString(FullTimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>A timestamp as the Time column shows it.</summary>
    public static string Time(DateTimeOffset timestamp) => timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>A timestamp with its offset, as the detail pane shows it.</summary>
    public static string FullTime(DateTimeOffset timestamp) => timestamp.ToString(FullTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The Time cell of <paramref name="e"/>: its timestamp, or, in a relative mode, the time elapsed since <paramref name="reference"/> (the
    /// event before it, or the selected one). The reference itself and rows without a usable reference keep their timestamp, so the list stays anchored.
    /// </summary>
    public static string TimeCell(LogEvent e, LogEvent? reference, TimeColumnMode mode)
    {
        ArgumentNullException.ThrowIfNull(e);
        return mode == TimeColumnMode.Timestamp || e.IsUnparsed || reference is null || reference.IsUnparsed || ReferenceEquals(e, reference)
            ? Time(e)
            : Delta(e.Timestamp - reference.Timestamp);
    }

    /// <summary>A signed elapsed time, as short as it can be without hiding the milliseconds that matter: <c>+120 ms</c>, <c>+3.204 s</c>, <c>-2 m 05 s</c>.</summary>
    public static string Delta(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "-" : "+";
        var d = delta.Duration();
        var inv = CultureInfo.InvariantCulture;
        if (d < TimeSpan.FromSeconds(1))
        {
            return string.Create(inv, $"{sign}{(long)d.TotalMilliseconds} ms");
        }

        if (d < TimeSpan.FromMinutes(1))
        {
            return string.Create(inv, $"{sign}{d.Seconds}.{d.Milliseconds:000} s");
        }

        if (d < TimeSpan.FromHours(1))
        {
            return string.Create(inv, $"{sign}{d.Minutes} m {d.Seconds:00} s");
        }

        return d < TimeSpan.FromDays(1)
            ? string.Create(inv, $"{sign}{d.Hours} h {d.Minutes:00} m")
            : string.Create(inv, $"{sign}{(long)d.TotalDays} d {d.Hours:00} h");
    }

    /// <summary>Three-letter level text for the badge; never empty, so the level is never conveyed by colour alone (UX-5).</summary>
    public static string LevelAbbreviation(LogLevel level) => level switch
    {
        LogLevel.Verbose => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        LogLevel.Unparsed => "—",
        _ => "???",
    };

    /// <summary>The level spelled out, for screen readers and tooltips.</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Unparsed => "Unparsed",
        LogLevel.Unknown => "Unknown level",
        _ => level.ToString(),
    };

    /// <summary>The grid shows one line per event; the full text is in the detail pane.</summary>
    public static string FirstLine(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }
}
