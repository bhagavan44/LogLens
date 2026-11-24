using System.Globalization;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.Core.Collections;

/// <summary>
/// Row height, text size and column widths of the log grid (FR-G1, FR-G6, FR-P2). Every row has this one height, so the grid keeps counting
/// rows, not pixels. A hidden column is 0 wide and a dragged one keeps the user's width.
/// </summary>
/// <param name="RowHeight">Height of every row; taller by whole text lines when long messages wrap.</param>
/// <param name="TimeColumnWidth">0 when the column is hidden.</param>
/// <param name="LevelColumnWidth">0 when the column is hidden.</param>
/// <param name="SourceColumnWidth">0 when the column is hidden or the log has no Source column.</param>
/// <param name="MessageLines">How many lines of a message a row shows.</param>
/// <param name="FontSize">Size of the log text in the rows.</param>
public readonly record struct GridMetrics(
    double RowHeight,
    double TimeColumnWidth,
    double LevelColumnWidth,
    double SourceColumnWidth,
    int MessageLines = 1,
    double FontSize = GridMetrics.DefaultFontSize)
{
    public const double DefaultFontSize = AppSettings.DefaultFontSize;

    /// <summary>Height of a one-line row at the default font size and comfortable density.</summary>
    public const double BaseRowHeight = 24;

    /// <summary>Fits <c>yyyy-MM-dd HH:mm:ss.fff</c> at the default font size with tabular numerals.</summary>
    public const double DefaultTimeColumnWidth = 160;

    public const double DefaultLevelColumnWidth = 40;

    /// <summary>The file name column of a merged view (FR-O4) until the user drags it.</summary>
    public const double DefaultSourceColumnWidth = 160;

    /// <summary>Lines a message can use when wrapping is on. Two keeps long messages readable without making rows tall.</summary>
    public const int WrappedMessageLines = 2;

    /// <summary>Room one text line takes inside a row at the default font size.</summary>
    public const double LineHeight = 16;

    /// <summary>Average advance of a digit in Inter with tabular numerals, as a fraction of the font size.</summary>
    private const double DigitWidth = DefaultTimeColumnWidth / 23 / DefaultFontSize;

    /// <summary>A timestamp with every field at its widest, to size the Time column for any format.</summary>
    private static readonly DateTimeOffset Widest = new(2026, 12, 28, 23, 59, 58, 999, TimeSpan.FromHours(-10));

    public static GridMetrics Default { get; } = For(null, wrapMessages: false, hasSource: false);

    /// <summary>The room one text line takes at <see cref="FontSize"/>.</summary>
    public double TextLineHeight => LineHeightFor(FontSize);

    /// <param name="columns">The user's layout; null for the defaults.</param>
    /// <param name="wrapMessages">Long messages use <see cref="WrappedMessageLines"/> lines (FR-G6).</param>
    /// <param name="hasSource">The log has a Source column (a merged view); other logs never show one.</param>
    /// <param name="fontSize">Size of the log text (FR-P2); clamped to what the settings accept.</param>
    /// <param name="density">Compact rows have less space around the text (FR-P2).</param>
    /// <param name="timeFormat">Format of the Time column (FR-G5); the column is sized to fit it unless the user dragged it.</param>
    public static GridMetrics For(
        ColumnLayout? columns,
        bool wrapMessages,
        bool hasSource,
        double fontSize = DefaultFontSize,
        RowDensity density = RowDensity.Comfortable,
        string? timeFormat = null)
    {
        var layout = (columns ?? new ColumnLayout()).Normalised();
        var size = double.IsFinite(fontSize) ? Math.Clamp(fontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize) : DefaultFontSize;
        var lines = wrapMessages ? WrappedMessageLines : 1;

        // Comfortable: 24 px at 12 px text; compact: 18 px. The padding scales with the text so large fonts are not cramped.
        var oneLine = density == RowDensity.Compact
            ? Math.Max(size + 5, Math.Round(size * 1.5))
            : Math.Max(size + 8, Math.Round(size * 2));
        var row = oneLine + ((lines - 1) * LineHeightFor(size));

        return new GridMetrics(
            row,
            layout.ShowTime ? layout.TimeWidth ?? TimeWidthFor(size, timeFormat) : 0,
            layout.ShowLevel ? Math.Round(DefaultLevelColumnWidth * size / DefaultFontSize) : 0,
            hasSource && layout.ShowSource ? layout.SourceWidth ?? DefaultSourceColumnWidth : 0,
            lines,
            size);
    }

    private static double LineHeightFor(double size) => Math.Ceiling(size * LineHeight / DefaultFontSize);

    /// <summary>Wide enough for the widest timestamp <paramref name="format"/> can produce at <paramref name="size"/>.</summary>
    private static double TimeWidthFor(double size, string? format)
    {
        var sample = Widest.ToString(TimeDisplay.IsUsableFormat(format) ? format : TimeDisplay.DefaultFormat, CultureInfo.InvariantCulture);
        return Math.Clamp(Math.Round(Math.Max(sample.Length, 4) * DigitWidth * size), ColumnLayout.MinWidth, ColumnLayout.MaxWidth);
    }
}
