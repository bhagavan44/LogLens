using LogLens.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace LogLens.App.Views;

/// <summary>One visible row of the <see cref="LogGrid"/>. Rows are pooled and re-bound while scrolling.</summary>
public sealed partial class LogRow : UserControl
{
    private bool _selected;

    public LogRow()
    {
        InitializeComponent();
        RowRoot.Height = LogFormat.RowHeight;
        TimeColumn.Width = new GridLength(LogFormat.TimeColumnWidth);
        LevelColumn.Width = new GridLength(LogFormat.LevelColumnWidth);
        foreach (var text in new[] { TimeText, MessageText })
        {
            text.FontFamily = LogFormat.TextFont;
            text.FontSize = LogFormat.FontSize;
        }

        // Inter is proportional: tabular numerals keep the timestamps lined up in a column.
        Typography.SetNumeralAlignment(TimeText, FontNumeralAlignment.Tabular);
    }

    /// <summary>The grid this row belongs to (for UI Automation).</summary>
    internal LogGrid? Owner { get; set; }

    /// <summary>Which row of the list this visual currently shows; -1 when unused.</summary>
    internal int Index { get; set; } = -1;

    protected override AutomationPeer OnCreateAutomationPeer() => new LogRowAutomationPeer(this);

    public void Bind(LogEvent e, bool selected, bool active)
    {
        var time = LogText.Time(e);
        var message = LogText.FirstLine(e.Message);

        TimeText.Text = time;
        LevelText.Text = LogText.LevelAbbreviation(e.Level);
        LevelBadge.Background = LogFormat.BadgeBackground(e.Level);
        LevelText.Foreground = LogFormat.BadgeForeground(e.Level);
        MessageText.Text = message;
        ExceptionIcon.Visibility = e.Exception is null ? Visibility.Collapsed : Visibility.Visible;

        _selected = selected;
        SelectedBg.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        HoverBg.Visibility = Visibility.Collapsed;
        FocusRect.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // Screen readers get the level spelled out (UX-5: never colour alone, and "ERR" reads poorly).
        AutomationProperties.SetName(this, $"{LogText.LevelName(e.Level)}, {time}, {message}{(e.Exception is null ? string.Empty : ", has exception")}");
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_selected)
        {
            HoverBg.Visibility = Visibility.Visible;
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => HoverBg.Visibility = Visibility.Collapsed;
}
