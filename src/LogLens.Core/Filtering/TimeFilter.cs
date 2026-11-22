using System.Globalization;
using System.Text.RegularExpressions;

namespace LogLens.Core.Filtering;

/// <summary>An inclusive wall-clock time window; either end may be open (FR-S2).</summary>
/// <remarks>
/// Wall-clock times are what the Time column shows. Until the timestamp zone option arrives (FR-G5) that is the time as logged, i.e. the
/// clock reading in the event's own offset (<see cref="WallClock"/>).
/// </remarks>
public sealed record TimeRange(DateTime? From, DateTime? To)
{
    public static TimeRange Open { get; } = new(null, null);

    public bool IsOpen => From is null && To is null;

    public bool Contains(DateTime wall) => (From is null || wall >= From) && (To is null || wall <= To);

    /// <summary>The window of <paramref name="span"/> ending at <paramref name="end"/> ("the last 5 minutes of the log").</summary>
    public static TimeRange Last(DateTime end, TimeSpan span) => new(end - span, null);

    /// <summary>The window of ± <paramref name="radius"/> around <paramref name="centre"/>.</summary>
    public static TimeRange Around(DateTime centre, TimeSpan radius) => new(centre - radius, centre + radius);

    /// <summary>The wall-clock reading of <paramref name="timestamp"/> as the grid shows it.</summary>
    public static DateTime WallClock(DateTimeOffset timestamp) => timestamp.DateTime;
}

/// <summary>A timestamp typed by the user: a wall-clock reading, or (when it carries <c>Z</c> / an offset) an exact instant.</summary>
public readonly record struct TimeLiteralValue(DateTime Wall, DateTimeOffset? Instant, bool HasDate);

/// <summary>Parses the timestamp forms accepted by the query bar, the time-range boxes and Go to.</summary>
public static partial class TimeLiteral
{
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
    ];

    private static readonly string[] TimeOnlyFormats = ["HH:mm:ss.FFFFFFF", "HH:mm:ss", "HH:mm"];

    /// <summary>
    /// Accepts <c>2026-11-13 08:00:05.301</c>, <c>2026-11-13T08:00</c>, <c>2026-11-13</c>, those with <c>Z</c> / <c>+05:30</c>, and
    /// time-only <c>08:00:05</c>, which takes its date from <paramref name="referenceDate"/> (without one,
    /// <see cref="TimeLiteralValue.HasDate"/> is false).
    /// </summary>
    public static bool TryParse(string? text, DateTime? referenceDate, out TimeLiteralValue value)
    {
        value = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.EndsWith('Z') || text.EndsWith('z') || TrailingOffset().IsMatch(text))
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            {
                value = new TimeLiteralValue(instant.DateTime, instant, true);
                return true;
            }

            return false;
        }

        if (DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            value = new TimeLiteralValue(wall, null, true);
            return true;
        }

        if (DateTime.TryParseExact(text, TimeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOnly))
        {
            value = referenceDate is { } date
                ? new TimeLiteralValue(date.Date + timeOnly.TimeOfDay, null, true)
                : new TimeLiteralValue(DateTime.MinValue + timeOnly.TimeOfDay, null, false);
            return true;
        }

        return false;
    }

    // "…08:00:05+05:30" / "…08:00-08:00", but not the dashes of a plain date such as 2026-11-13.
    [GeneratedRegex(@"(?<=\d)[+-]\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOffset();
}
