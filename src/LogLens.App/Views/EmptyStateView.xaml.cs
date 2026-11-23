using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Shown when no file is open (UX-2): the drop zone, an Open button and the recent files.</summary>
public sealed partial class EmptyStateView : UserControl
{
    public EmptyStateView()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenRequested;

    /// <summary>A recent file was clicked; carries its path.</summary>
    public event EventHandler<string>? RecentFileRequested;

    /// <summary>Shows <paramref name="files"/> (newest first) as links; the list is hidden when it is empty.</summary>
    public void SetRecentFiles(IReadOnlyList<string> files)
    {
        RecentList.Children.Clear();
        foreach (var path in files)
        {
            var link = new HyperlinkButton
            {
                Content = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = path,
            };
            ToolTipService.SetToolTip(link, path);
            AutomationProperties.SetName(link, $"Open {Path.GetFileName(path)}");
            AutomationProperties.SetHelpText(link, path);
            link.Click += (sender, _) => RecentFileRequested?.Invoke(this, (string)((FrameworkElement)sender).Tag);
            RecentList.Children.Add(link);
        }

        RecentPanel.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
}
