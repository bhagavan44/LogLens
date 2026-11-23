using System.Globalization;
using LogLens.Core.Export;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Export (FR-E1): which format, and the events shown in the list or just the selected rows.</summary>
public sealed partial class ExportDialog : ContentDialog
{
    private static readonly ExportFormat[] Formats = [ExportFormat.Csv, ExportFormat.Json, ExportFormat.Html, ExportFormat.Text, ExportFormat.Markdown];

    public ExportDialog(int listedCount, int selectedCount, ExportFormat initial)
    {
        InitializeComponent();
        foreach (var format in Formats)
        {
            FormatBox.Items.Add(LogExporter.DisplayName(format));
        }

        FormatBox.SelectedIndex = Array.IndexOf(Formats, initial) is var i and >= 0 ? i : 0;

        ListedRadio.Content = string.Create(CultureInfo.InvariantCulture, $"All events in the list ({listedCount:N0})");
        SelectedRadio.Content = string.Create(CultureInfo.InvariantCulture, $"Only the selected rows ({selectedCount:N0})");

        SelectedRadio.IsEnabled = selectedCount >= 1;
        ListedRadio.IsChecked = true;
        IsPrimaryButtonEnabled = listedCount > 0;
    }

    public ExportFormat Format => Formats[Math.Max(0, FormatBox.SelectedIndex)];

    public bool SelectionOnly => SelectedRadio.IsChecked == true;

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => FormatHint.Text = Format switch
    {
        ExportFormat.Csv => "Opens in Excel. Text that starts with = + - or @ gets a leading ' so a spreadsheet never runs it as a formula.",
        ExportFormat.Json => "One JSON object per line with @t (time), @l (level), @m (message) and @x (exception).",
        ExportFormat.Html => "A single self-contained page with coloured levels. Nothing is loaded from the internet.",
        ExportFormat.Text => "The original lines exactly as they are in the log file.",
        _ => "A table you can paste into a ticket or chat.",
    };
}
