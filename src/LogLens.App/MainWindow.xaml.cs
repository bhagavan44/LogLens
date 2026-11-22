using System.ComponentModel;
using LogLens.App.Views;
using LogLens.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace LogLens.App;

/// <summary>The main window: title bar, menu, message bar, and either the empty state or the open log. State lives in <see cref="MainViewModel"/>.</summary>
public sealed partial class MainWindow : Window
{
    private LogView? _logView;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // UX-7: Mica on Windows 11; a solid themed background where it isn't supported (Windows 10).
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncAll();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Opens files given on the command line or dropped on the window.</summary>
    public void Open(IReadOnlyList<string> paths) => _ = ViewModel.OpenAsync(paths);

    public void ReportUnexpectedError(Exception exception) => ViewModel.ReportUnexpectedError(exception);

    // ---------------------------------------------------------------- view-model → view

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentLog):
                _logView = ViewModel.CurrentLog is { } log ? new LogView(log) : null;
                LogHost.Content = _logView;
                break;
            case nameof(MainViewModel.LoadProgress):
                LoadingProgress.Value = ViewModel.LoadProgress;
                return;
        }

        SyncAll();
    }

    private void SyncAll()
    {
        Title = ViewModel.WindowTitle;
        TitleText.Text = ViewModel.WindowTitle;
        CloseItem.IsEnabled = ViewModel.HasLog;
        foreach (var item in new[] { FindItem, FindNextItem, FindPreviousItem, FilterItem, ClearFiltersItem, GoToItem, NextProblemItem, PreviousProblemItem })
        {
            item.IsEnabled = ViewModel.HasLog;
        }

        EmptyState.Visibility = ViewModel.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = ViewModel.LoadingText;
        LoadingProgress.Value = ViewModel.LoadProgress;

        if (ViewModel.Notice is { } notice)
        {
            NoticeBar.Severity = notice.Severity switch
            {
                NoticeSeverity.Error => InfoBarSeverity.Error,
                NoticeSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };
            NoticeBar.Title = notice.Title;
            NoticeBar.Message = notice.Message;
            NoticeBar.IsOpen = true;
        }
        else
        {
            NoticeBar.IsOpen = false;
        }
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Notice = null;

    // ---------------------------------------------------------------- commands

    private async void OnOpen(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async void OnEmptyStateOpen(object? sender, EventArgs e) => await PickAndOpenAsync();

    private void OnClose(object sender, RoutedEventArgs e) => ViewModel.Close();

    private void OnCancelLoad(object sender, RoutedEventArgs e) => ViewModel.CancelLoad();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnFind(object sender, RoutedEventArgs e) => _logView?.FocusFind();

    private void OnFindNext(object sender, RoutedEventArgs e) => _logView?.FindNext(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => _logView?.FindNext(forward: false);

    private void OnFocusFilter(object sender, RoutedEventArgs e) => _logView?.FocusQuery();

    private void OnClearFilters(object sender, RoutedEventArgs e) => _logView?.ClearFilters();

    private async void OnGoTo(object sender, RoutedEventArgs e)
    {
        if (_logView is { } view)
        {
            await view.GoToAsync();
        }
    }

    private void OnNextProblem(object sender, RoutedEventArgs e) => _logView?.GoToProblem(forward: true);

    private void OnPreviousProblem(object sender, RoutedEventArgs e) => _logView?.GoToProblem(forward: false);

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"{AppInfo.Name} {AppInfo.Version}",
            Content = "An offline, read-only log viewer for Windows.\n\nLogLens never changes the files it opens, sends no telemetry and makes no network calls.\n\nLicensed under the GNU GPL v3.0. The Inter font is licensed under the SIL Open Font License 1.1.",
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in new[] { ".log", ".txt", ".clef", ".json", "*" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.OpenAsync([file.Path]);
        }
    }

    // ---------------------------------------------------------------- drag and drop (FR-O1)

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open log file";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        List<string> files;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            files = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (files.Count == 0 && items.Count > 0)
            {
                ViewModel.Notice = new Notice(NoticeSeverity.Informational, "Drop a file", "Opening a folder arrives in a later version; drop a log file instead.");
                return;
            }
        }
        finally
        {
            deferral.Complete();
        }

        await ViewModel.OpenAsync(files);
    }
}
