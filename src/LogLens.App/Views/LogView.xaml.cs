using LogLens.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LogLens.App.Views;

/// <summary>One open log: the event grid, the detail pane and the status bar. All state lives in <see cref="LogViewModel"/>.</summary>
public sealed partial class LogView : UserControl
{
    public LogView(LogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        HeaderTimeColumn.Width = new GridLength(LogFormat.TimeColumnWidth);
        HeaderLevelColumn.Width = new GridLength(LogFormat.LevelColumnWidth);
        MessageBox.FontFamily = LogFormat.TextFont;
        ExceptionBox.FontFamily = LogFormat.CodeFont;
        RawBox.FontFamily = LogFormat.CodeFont;
        foreach (var box in new[] { MessageBox, ExceptionBox, RawBox })
        {
            box.FontSize = LogFormat.FontSize;
        }

        EventGrid.Source = viewModel.Events;
        Loaded += (_, _) => EventGrid.Focus(FocusState.Programmatic);
    }

    public LogViewModel ViewModel { get; }

    private void OnGridSelectionChanged(object? sender, EventArgs e) => ViewModel.SelectedEvent = EventGrid.ActiveEvent;

    private void OnDetailTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        MessageBox.Visibility = selected == MessageTab ? Visibility.Visible : Visibility.Collapsed;
        ExceptionBox.Visibility = selected == ExceptionTab ? Visibility.Visible : Visibility.Collapsed;
        RawBox.Visibility = selected == RawTab ? Visibility.Visible : Visibility.Collapsed;
        CopyExceptionButton.Visibility = ExceptionBox.Visibility;
    }

    private void OnCopyRequested(object? sender, bool raw) => CopySelection(raw);

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection(raw: false);

    private void OnCopyRawClick(object sender, RoutedEventArgs e) => CopySelection(raw: true);

    private void OnCopyExceptionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEvent?.Exception is { } exception)
        {
            SetClipboard(exception);
        }
    }

    private void CopySelection(bool raw)
    {
        if (EventGrid.SelectedCount > 0)
        {
            SetClipboard(ViewModel.BuildCopyText(EventGrid.SelectedEvents(), raw));
        }
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
