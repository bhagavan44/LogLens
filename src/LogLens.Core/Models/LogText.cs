using System.Globalization;

namespace LogLens.Core.Models;

/// <summary>
/// How an event is rendered as text in the grid, the detail pane and the clipboard. Kept in Core so it is unit-tested; the WinUI layer only
/// adds colours. Timestamps are shown as logged (zone and format options arrive with FR-G5).
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
