using System.Globalization;

namespace LogLens.Core.Models;

/// <summary>Which clock timestamps are shown in (FR-G5, FR-P3).</summary>
public enum TimestampZone
{
    /// <summary>The clock reading in the event's own offset, exactly as the file has it.</summary>
    AsLogged = 0,

    /// <summary>Converted to this PC's time zone.</summary>
    Local = 1,

    /// <summary>Converted to UTC.</summary>
    Utc = 2,
}

/// <summary>
/// How timestamps are shown (FR-G5, FR-P3): the zone and the format of the Time column. The wall-clock time it gives
/// (<see cref="ToWall"/>) is also what the time filter, the query bar's <c>@t</c>, the timeline and Go to compare against, so what the user
/// types matches what the grid shows. Immutable; a view-model swaps in a new one when the settings change.
/// </summary>
public sealed record TimeDisplay
{
    /// <summary>Format of the Time column unless the user picks another; includes the date so multi-day logs stay unambiguous.</summary>
    public const string DefaultFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Format of the full timestamp in the detail pane.</summary>
    public const string FullFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    /// <summary>A custom format longer than this is refused: it is a mistake, not a timestamp.</summary>
    public const int MaxFormatLength = 64;

    public TimeDisplay(TimestampZone zone = TimestampZone.AsLogged, string? format = null)
    {
        Zone = Enum.IsDefined(zone) ? zone : TimestampZone.AsLogged;
        Format = IsUsableFormat(format) ? format! : DefaultFormat;
    }

    /// <summary>Timestamps as logged, in <see cref="DefaultFormat"/>.</summary>
    public static TimeDisplay Default { get; } = new();

    public TimestampZone Zone { get; }

    /// <summary>A .NET custom date and time format; always usable (an unusable one is replaced by <see cref="DefaultFormat"/>).</summary>
    public string Format { get; }

    /// <summary><paramref name="timestamp"/> in the chosen zone.</summary>
    public DateTimeOffset Convert(DateTimeOffset timestamp) => Zone switch
    {
        TimestampZone.Local => timestamp.ToLocalTime(),
        TimestampZone.Utc => timestamp.ToUniversalTime(),
        _ => timestamp,
    };

    /// <summary>The wall-clock reading the grid shows for <paramref name="timestamp"/>.</summary>
    public DateTime ToWall(DateTimeOffset timestamp) => Convert(timestamp).DateTime;

    /// <summary>The Time column text of <paramref name="e"/>; "—" for an unparsed line.</summary>
    public string Time(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsUnparsed ? "—" : Time(e.Timestamp);
    }

    public string Time(DateTimeOffset timestamp) => Convert(timestamp).ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>The detail pane's timestamp: date, time to the millisecond and the offset, in the chosen zone.</summary>
    public string FullTime(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsUnparsed ? "no timestamp" : FullTime(e.Timestamp);
    }

    public string FullTime(DateTimeOffset timestamp) => Convert(timestamp).ToString(FullFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The Time cell of <paramref name="e"/>: its timestamp, or, in a relative mode, the time elapsed since <paramref name="reference"/> (the
    /// event before it, or the selected one). The reference itself and rows without a usable reference keep their timestamp, so the list stays anchored.
    /// </summary>
    public string TimeCell(LogEvent e, LogEvent? reference, TimeColumnMode mode)
    {
        ArgumentNullException.ThrowIfNull(e);
        return mode == TimeColumnMode.Timestamp || e.IsUnparsed || reference is null || reference.IsUnparsed || ReferenceEquals(e, reference)
            ? Time(e)
            : LogText.Delta(e.Timestamp - reference.Timestamp);
    }

    /// <summary>True when <paramref name="format"/> formats a timestamp without throwing and is not absurdly long.</summary>
    public static bool IsUsableFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > MaxFormatLength)
        {
            return false;
        }

        try
        {
            _ = DateTimeOffset.UnixEpoch.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
