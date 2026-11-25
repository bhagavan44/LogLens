using LogLens.App.Services;
using LogLens.App.Views;
using LogLens.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LogLens.App;

/// <summary>The Settings dialog (FR-P1 … FR-P6), applying the settings to the window, and the keyboard shortcut list (UX-1, F1).</summary>
public sealed partial class MainWindow
{
    private ThemePreference? _appliedTheme;

    /// <summary>What the window itself takes from the settings: the theme (FR-P1) and whether new files start tailing (FR-P3).</summary>
    private void ApplyWindowSettings(AppSettings settings)
    {
        if (_appliedTheme != settings.Theme)
        {
            _appliedTheme = settings.Theme;
            ThemeService.Apply(this, Root, settings.Theme);
        }

        ViewModel.TailByDefault = settings.TailByDefault;
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var previous = _settings.Current;
        var dialog = new SettingsDialog(previous, _paths, WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // A Time column dragged to fit the old font size or format goes back to fitting the new one, so it is not cut off.
        var next = dialog.Result.WithMachineStateOf(_settings.Current);
        if (!next.FontSize.Equals(previous.FontSize) || next.TimestampFormat != previous.TimestampFormat)
        {
            next = next with { Columns = next.Columns with { TimeWidth = null } };
        }

        _settings.Update(_ => next); // every tab and the window follow through SettingsService.Changed
        _log.Information("Settings saved");
    }

    private async void OnShortcuts(object sender, RoutedEventArgs e)
    {
        var shortcuts = new (string Keys, string Action)[]
        {
            ("Ctrl+O", "Open files"),
            ("Ctrl+Shift+O", "Open folder"),
            ("Ctrl+W", "Close tab"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous tab"),
            ("Ctrl+S", "Export events"),
            ("Ctrl+F", "Find"),
            ("F3 / Shift+F3", "Next / previous match"),
            ("Ctrl+Shift+F", "Find all events containing…"),
            ("Ctrl+L", "Filter query"),
            ("Enter / Esc (in the query bar)", "Apply the query now / clear it"),
            ("Ctrl+G", "Go to event number or time"),
            ("Ctrl+E / Ctrl+Shift+E", "Next / previous warning or error"),
            ("Ctrl+F2", "Toggle bookmark"),
            ("F2 / Shift+F2", "Next / previous bookmark"),
            ("Ctrl+T", "Tail: show new events as they are written"),
            ("Ctrl+Shift+P", "Pause / resume the tail"),
            ("F5", "Refresh: read what was written since"),
            ("↑ ↓ PgUp PgDn Home End", "Move through events (Shift extends the selection)"),
            ("Ctrl+A", "Select all events"),
            ("Ctrl+C / Ctrl+Shift+C", "Copy rows / original lines"),
            ("Shift+F10 or the Menu key", "Row menu"),
            ("Ctrl+,", "Settings"),
            ("F1", "This list"),
        };

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = shortcuts[i].Keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true };
            var action = new TextBlock { Text = shortcuts[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
