using LogLens.Core.Settings;

namespace LogLens.Core.Collections;

/// <summary>
/// Row height and column widths of the log grid (FR-G1, FR-G6). Every row has this one height, so the grid keeps counting rows, not pixels.
/// A hidden column is 0 wide and a dragged one keeps the user's width.
/// </summary>
/// <param name="RowHeight">Height of every row; taller by whole text lines when long messages wrap.</param>
/// <param name="TimeColumnWidth">0 when the column is hidden.</param>
/// <param name="LevelColumnWidth">0 when the column is hidden.</param>
/// <param name="SourceColumnWidth">0 when the column is hidden or the log has no Source column.</param>
/// <param name="MessageLines">How many lines of a message a row shows.</param>
public readonly record struct GridMetrics(double RowHeight, double TimeColumnWidth, double LevelColumnWidth, double SourceColumnWidth, int MessageLines = 1)
{
    public const double FontSize = 12;

    /// <summary>Height of a one-line row.</summary>
    public const double BaseRowHeight = 24;

    /// <summary>Fits <c>yyyy-MM-dd HH:mm:ss.fff</c> at <see cref="FontSize"/> with tabular numerals.</summary>
    public const double DefaultTimeColumnWidth = 160;

    public const double DefaultLevelColumnWidth = 40;

    /// <summary>The file name column of a merged view (FR-O4) until the user drags it.</summary>
    public const double DefaultSourceColumnWidth = 160;

    /// <summary>Lines a message can use when wrapping is on. Two keeps long messages readable without making rows tall.</summary>
    public const int WrappedMessageLines = 2;

    /// <summary>Room one text line takes inside a row.</summary>
    public const double LineHeight = 16;

    public static GridMetrics Default { get; } = For(null, wrapMessages: false, hasSource: false);

    /// <param name="columns">The user's layout; null for the defaults.</param>
    /// <param name="wrapMessages">Long messages use <see cref="WrappedMessageLines"/> lines (FR-G6).</param>
    /// <param name="hasSource">The log has a Source column (a merged view); other logs never show one.</param>
    public static GridMetrics For(ColumnLayout? columns, bool wrapMessages, bool hasSource)
    {
        var layout = (columns ?? new ColumnLayout()).Normalised();
        var lines = wrapMessages ? WrappedMessageLines : 1;
        return new GridMetrics(
            BaseRowHeight + ((lines - 1) * LineHeight),
            layout.ShowTime ? layout.TimeWidth ?? DefaultTimeColumnWidth : 0,
            layout.ShowLevel ? DefaultLevelColumnWidth : 0,
            hasSource && layout.ShowSource ? layout.SourceWidth ?? DefaultSourceColumnWidth : 0,
            lines);
    }
}
