using System.Globalization;
using LogLens.Infrastructure.Loading;
using LogLens.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Open folder (FR-O3): lists the log files of a folder, filtered by a pattern, to open in tabs or merge into one view.</summary>
public sealed partial class OpenFolderDialog : ContentDialog
{
    private readonly string _folder;

    public OpenFolderDialog(string folder, string patterns)
    {
        _folder = folder;
        InitializeComponent();
        FolderText.Text = folder;
        PatternBox.Text = patterns;
        Loaded += (_, _) => Refresh();
    }

    /// <summary>The pattern the user ended up with, so it is offered again next time.</summary>
    public string Patterns => string.IsNullOrWhiteSpace(PatternBox.Text) ? FolderScanner.DefaultPatterns : PatternBox.Text.Trim();

    /// <summary>The chosen files, by path (which is date order for rolling logs).</summary>
    public IReadOnlyList<string> SelectedPaths
        => [.. FileList.SelectedItems.OfType<ListViewItem>().Select(i => (string)i.Tag).Order(StringComparer.OrdinalIgnoreCase)];

    private void Refresh()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        FileList.Items.Clear();
        var subfolders = SubfoldersBox.IsChecked == true;
        IReadOnlyList<LogFileInfo> files;
        try
        {
            files = FolderScanner.Enumerate(_folder, PatternBox.Text, subfolders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorText.Text = ex is UnauthorizedAccessException or System.Security.SecurityException
                ? "Access to this folder was denied."
                : "This folder can't be read: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            files = [];
        }

        foreach (var file in files)
        {
            var name = subfolders ? Path.GetRelativePath(_folder, file.Path) : file.Name;
            var size = LogViewModel.FormatSize(file.Length);
            var when = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var sizeText = new TextBlock { Text = size, Opacity = 0.7, MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sizeText, 1);
            row.Children.Add(sizeText);
            var whenText = new TextBlock { Text = when, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(whenText, 2);
            row.Children.Add(whenText);

            var item = new ListViewItem { Content = row, Tag = file.Path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, $"{name}, {size}, changed {when}");
            ToolTipService.SetToolTip(item, file.Path);
            FileList.Items.Add(item);
        }

        UpdateState();
    }

    private void UpdateState()
    {
        var total = FileList.Items.Count;
        var selected = FileList.SelectedItems.Count;
        CountText.Text = total == 0
            ? "No matching files in this folder."
            : string.Create(CultureInfo.CurrentCulture, $"{selected:N0} of {total:N0} chosen");
        IsPrimaryButtonEnabled = selected >= 1;
        IsSecondaryButtonEnabled = selected >= 2;
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Refresh();
        }
    }

    private void OnSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Refresh();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => FileList.SelectAll();

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => FileList.SelectedItems.Clear();
}
