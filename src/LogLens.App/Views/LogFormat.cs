using LogLens.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LogLens.App.Views;

/// <summary>
/// The WinUI side of how events look: level badge colours, fonts and grid metrics. Text comes from <see cref="LogText"/> (unit-tested).
/// </summary>
public static class LogFormat
{
    /// <summary>Height of every grid row: rows never differ in height, so the grid counts rows, not pixels.</summary>
    public const double RowHeight = 24;

    public const double FontSize = 12;

    /// <summary>Fits <see cref="LogText.TimeFormat"/> at <see cref="FontSize"/> with tabular numerals.</summary>
    public const double TimeColumnWidth = 160;

    public const double LevelColumnWidth = 40;

    /// <summary>The file name column of a merged view (FR-O4).</summary>
    public const double SourceColumnWidth = 160;

    /// <summary>The Inter file shipped with the app, so the default font does not depend on what is installed.</summary>
    public static FontFamily TextFont { get; } = new("ms-appx:///Assets/Fonts/Inter.ttf#Inter, Segoe UI");

    /// <summary>Exception and Raw text rely on alignment, so they use a monospaced font.</summary>
    public static FontFamily CodeFont { get; } = new("Cascadia Mono, Consolas");

    /// <summary>
    /// Badge background and text colour per level (FR-G2). Each pair has at least 4.5:1 contrast on its own, so the badge meets WCAG AA in
    /// light and dark themes alike; the level is always written in the badge too, never shown by colour alone (UX-5).
    /// </summary>
    private static readonly (Brush Background, Brush Foreground)[] Badges =
    [
        Badge(0x8A, 0x8A, 0x8A, dark: true),   // Unknown      (black on grey 6.2:1)
        Badge(0x6B, 0x6B, 0x6B, dark: false),  // Verbose      (white on grey 5.3:1)
        Badge(0x4F, 0x6A, 0x78, dark: false),  // Debug        (white on slate 5.9:1)
        Badge(0x15, 0x65, 0xC0, dark: false),  // Information  (white on blue 5.8:1)
        Badge(0xF2, 0xB1, 0x00, dark: true),   // Warning      (black on amber 10.5:1)
        Badge(0xC6, 0x28, 0x28, dark: false),  // Error        (white on red 5.6:1)
        Badge(0x7B, 0x10, 0x43, dark: false),  // Fatal        (white on plum 10.9:1)
        (new SolidColorBrush(Colors.Transparent), new SolidColorBrush(Color.FromArgb(255, 0x80, 0x80, 0x80))), // Unparsed: no badge
    ];

    public static Brush BadgeBackground(LogLevel level) => Badges[(int)level].Background;

    public static Brush BadgeForeground(LogLevel level) => Badges[(int)level].Foreground;

    private static (Brush, Brush) Badge(byte r, byte g, byte b, bool dark)
        => (new SolidColorBrush(Color.FromArgb(255, r, g, b)), new SolidColorBrush(dark ? Color.FromArgb(255, 0x14, 0x14, 0x14) : Colors.White));
}
