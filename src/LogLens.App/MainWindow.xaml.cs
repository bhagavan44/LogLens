using System.Collections.Specialized;
using System.ComponentModel;
using LogLens.App.Services;
using LogLens.App.Views;
using LogLens.Core.Models;
using LogLens.Core.Settings;
using LogLens.Infrastructure;
using LogLens.Infrastructure.Instances;
using LogLens.Infrastructure.Loading;
using LogLens.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace LogLens.App;

/// <summary>
/// The main window: the tab strip in the title bar (FR-O2), the menu, the message bar, and either the empty state or the log in front. State
/// lives in <see cref="MainViewModel"/>; this class keeps one tab header and one <see cref="LogView"/> per open log in step with it.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string[] OpenExtensions = [".log", ".txt", ".clef", ".json", "*"];

    private readonly Dictionary<LogViewModel, (TabViewItem Tab, LogView View)> _open = [];
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly Serilog.ILogger _log;
    private readonly Infrastructure.Remote.SftpConnections _remote;
    private readonly Infrastructure.Remote.IHostKeyTrust _hostKeys;
    private LogViewModel? _observedLog;
    private bool _syncingTabs;
    private string _folderPatterns = FolderScanner.DefaultPatterns;

    // What the Time column shows (View ▸ Time column, FR-G5). Not saved: every start shows timestamps again.
    private TimeColumnMode _timeMode = TimeColumnMode.Timestamp;

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settings,
        AppPaths paths,
        Serilog.ILogger log,
        Infrastructure.Remote.SftpConnections remote,
        Infrastructure.Remote.IHostKeyTrust hostKeys)
    {
        ViewModel = viewModel;
        _settings = settings;
        _paths = paths;
        _log = log;
        _remote = remote;
        _hostKeys = hostKeys;
        Closed += (_, _) => _remote.Dispose(); // closes the SSH connections; the passwords held for reconnecting go with them
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        Root.Loaded += (_, _) => UpdateTitleBarInset();
        Root.SizeChanged += (_, _) => UpdateTitleBarInset(); // also catches a change of display scale

        // UX-7: Mica on Windows 11; a solid themed background where it isn't supported (Windows 10).
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            SolidBackground.Visibility = Visibility.Visible;
        }

        AddTabSwitchAccelerators();
        SettingsItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = (VirtualKey)188 /* VK_OEM_COMMA */, Modifiers = VirtualKeyModifiers.Control });
        ApplyWindowSettings(_settings.Current);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Tabs.CollectionChanged += OnTabsCollectionChanged;
        ViewModel.TabChanged += (_, log) => UpdateTabHeader(log);
        _settings.Changed += OnSettingsChanged;
        SyncAll();
        SyncRecentFiles();
    }

    /// <summary>The settings changed (a view option, a column, the Settings dialog): the window and every tab take them.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyWindowSettings(_settings.Current);
        foreach (var (_, view) in _open.Values)
        {
            view.ApplySettings(_settings.Current);
        }

        SyncViewItems();
    }

    public MainViewModel ViewModel { get; }

    private LogView? CurrentView => ViewModel.CurrentLog is { } log && _open.TryGetValue(log, out var open) ? open.View : null;

    /// <summary>Opens files given on the command line or dropped on the window, each in a tab of its own, or as one merged view (<c>--merge</c>).</summary>
    public void Open(IReadOnlyList<string> paths, bool merge = false) => _ = merge ? ViewModel.OpenMergedAsync(paths) : ViewModel.OpenAsync(paths);

    /// <summary>Another start of LogLens handed its command line over (single instance): open its files here and come to the front.</summary>
    public void HandleLaunch(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BringToFront();
        var commandLine = request.Resolve();
        if (commandLine.Files.Count > 0)
        {
            Open(commandLine.Files, commandLine.Merge);
        }
    }

    private void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        NativeMethods.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void ReportUnexpectedError(Exception exception) => ViewModel.ReportUnexpectedError(exception);

    /// <summary>Keeps the drag area at the end of the tab strip as wide as the caption buttons, so tabs stop before them.</summary>
    private void UpdateTitleBarInset()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var inset = AppWindow.TitleBar.RightInset / scale;
        if (inset > 0 && Math.Abs(TitleDragRegion.MinWidth - inset) > 0.5)
        {
            TitleDragRegion.MinWidth = inset;
        }
    }

    // ---------------------------------------------------------------- view-model → view

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentLog):
                SyncSelectedTab();
                ObserveLog(ViewModel.CurrentLog);
                QueueFirstRunHints();
                break;
            case nameof(MainViewModel.RecentFiles):
                SyncRecentFiles();
                return;
        }

        SyncAll();
    }

    private void SyncAll()
    {
        Title = ViewModel.WindowTitle;
        TitleText.Visibility = ViewModel.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasLog = ViewModel.HasLog;
        foreach (var item in new[]
        {
            ExportItem, CloseItem, FindItem, FindNextItem, FindPreviousItem, FindAllItem, FilterItem, ClearFiltersItem, GoToItem, NextProblemItem,
            PreviousProblemItem, ToggleBookmarkItem, NextBookmarkItem, PreviousBookmarkItem,
        })
        {
            item.IsEnabled = hasLog;
        }

        CloseOthersItem.IsEnabled = NextTabItem.IsEnabled = PreviousTabItem.IsEnabled = ViewModel.Tabs.Count > 1;

        EmptyState.Visibility = ViewModel.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = ViewModel.LoadingText;
        SyncLiveItems();
        SyncBookmarkItems();
        SyncViewItems();

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

    // ---------------------------------------------------------------- tabs (FR-O2)

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncTabs();
        SyncAll();
    }

    /// <summary>Makes the tab strip show <see cref="MainViewModel.Tabs"/> in order, creating and dropping headers and views as needed.</summary>
    private void SyncTabs()
    {
        foreach (var closed in _open.Keys.Where(log => !ViewModel.Tabs.Contains(log)).ToList())
        {
            if (ReferenceEquals(LogHost.Content, _open[closed].View))
            {
                LogHost.Content = null;
            }

            _open.Remove(closed);
        }

        foreach (var log in ViewModel.Tabs.Where(log => !_open.ContainsKey(log)))
        {
            _open[log] = (CreateTab(log), new LogView(log, _settings) { TimeMode = _timeMode });
        }

        var wanted = ViewModel.Tabs.Select(log => (object)_open[log].Tab).ToList();
        var items = Tabs.TabItems;
        _syncingTabs = true;
        try
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(items[i]))
                {
                    items.RemoveAt(i);
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < items.Count && ReferenceEquals(items[i], wanted[i]))
                {
                    continue;
                }

                var at = items.IndexOf(wanted[i]);
                if (at >= 0)
                {
                    items.RemoveAt(at);
                }

                items.Insert(i, wanted[i]);
            }
        }
        finally
        {
            _syncingTabs = false;
        }

        SyncSelectedTab();
    }

    private TabViewItem CreateTab(LogViewModel log)
    {
        var tab = new TabViewItem { Tag = log, IsClosable = true };
        var close = new MenuFlyoutItem { Text = "Close tab", KeyboardAcceleratorTextOverride = "Ctrl+W" };
        close.Click += (_, _) => ViewModel.CloseTab(log);
        var closeOthers = new MenuFlyoutItem { Text = "Close other tabs" };
        closeOthers.Click += (_, _) => ViewModel.CloseOthers(log);
        var menu = new MenuFlyout { Items = { close, closeOthers } };
        menu.Opening += (_, _) => closeOthers.IsEnabled = ViewModel.Tabs.Count > 1;
        tab.ContextFlyout = menu;
        SetTabHeader(tab, log);
        return tab;
    }

    private void UpdateTabHeader(LogViewModel log)
    {
        if (_open.TryGetValue(log, out var open))
        {
            SetTabHeader(open.Tab, log);
        }

        if (ReferenceEquals(log, ViewModel.CurrentLog))
        {
            Title = ViewModel.WindowTitle;
        }
    }

    private static void SetTabHeader(TabViewItem tab, LogViewModel log)
    {
        var title = MainViewModel.TabTitle(log);
        tab.Header = title;
        ToolTipService.SetToolTip(tab, MainViewModel.TabToolTip(log));
        AutomationProperties.SetName(tab, log.IsMerged ? $"Merged view: {title}" : title);

        // A merged view carries the Segoe Fluent "Library" glyph (U+E8F1) so it stands out from single files.
        tab.IconSource = log.IsMerged ? new FontIconSource { Glyph = "" } : null;
    }

    /// <summary>Selects the tab of the log in front and shows its view.</summary>
    private void SyncSelectedTab()
    {
        var open = ViewModel.CurrentLog is { } log && _open.TryGetValue(log, out var found) ? found : default;
        if (!ReferenceEquals(Tabs.SelectedItem, open.Tab))
        {
            _syncingTabs = true;
            try
            {
                Tabs.SelectedItem = open.Tab;
            }
            finally
            {
                _syncingTabs = false;
            }
        }

        if (!ReferenceEquals(LogHost.Content, open.View))
        {
            LogHost.Content = open.View;
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTabs && Tabs.SelectedItem is TabViewItem { Tag: LogViewModel log })
        {
            ViewModel.CurrentLog = log;
        }
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is LogViewModel log)
        {
            ViewModel.CloseTab(log);
        }
    }

    /// <summary>A tab was dragged to another place: the view-model takes over the new order (and remembers it).</summary>
    private void OnTabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        if (_syncingTabs)
        {
            return;
        }

        var order = Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<LogViewModel>().ToList();
        if (order.Count != ViewModel.Tabs.Count || order.SequenceEqual(ViewModel.Tabs))
        {
            return; // half-way through a drag, or nothing moved
        }

        _syncingTabs = true;
        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var from = ViewModel.Tabs.IndexOf(order[i]);
                if (from >= 0 && from != i)
                {
                    ViewModel.MoveTab(from, i);
                }
            }
        }
        finally
        {
            _syncingTabs = false;
        }

        SyncSelectedTab();
    }

    private async void OnAddTabClick(TabView sender, object args) => await PickAndOpenAsync();

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab switch tabs from anywhere; inside the tab strip the TabView handles them itself.</summary>
    private void AddTabSwitchAccelerators()
    {
        foreach (var (modifiers, forward) in new[] { (VirtualKeyModifiers.Control, true), (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, false) })
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.Tab, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                if (!IsFocusInTabStrip())
                {
                    ViewModel.SelectNextTab(forward);
                    args.Handled = true;
                }
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private bool IsFocusInTabStrip()
    {
        if (Root.XamlRoot is null)
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, Tabs))
            {
                return true;
            }
        }

        return false;
    }

    private void OnNextTab(object sender, RoutedEventArgs e) => ViewModel.SelectNextTab(forward: true);

    private void OnPreviousTab(object sender, RoutedEventArgs e) => ViewModel.SelectNextTab(forward: false);

    // ---------------------------------------------------------------- the log in front

    private void ObserveLog(LogViewModel? log)
    {
        if (_observedLog is not null)
        {
            _observedLog.PropertyChanged -= OnLogPropertyChanged;
        }

        _observedLog = log;
        if (log is not null)
        {
            log.PropertyChanged += OnLogPropertyChanged;
        }
    }

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogViewModel.IsFollowing) or nameof(LogViewModel.IsPaused) or nameof(LogViewModel.FollowRolled)
            or nameof(LogViewModel.CanFollow) or nameof(LogViewModel.CanRefresh))
        {
            SyncLiveItems();
        }
        else if (e.PropertyName is nameof(LogViewModel.HasBookmarks) or nameof(LogViewModel.BookmarksPanelVisible))
        {
            SyncBookmarkItems();
        }
        else if (e.PropertyName is nameof(LogViewModel.StatisticsVisible) or nameof(LogViewModel.Format))
        {
            SyncViewItems();
        }
    }

    /// <summary>Ticks the View menu items that show an option: the settings, the time column mode, and the statistics of the log in front.</summary>
    private void SyncViewItems()
    {
        var settings = _settings.Current;
        var log = ViewModel.CurrentLog;
        TimelineItem.IsChecked = settings.ShowTimeline;
        NewestFirstItem.IsChecked = settings.NewestFirst;
        WrapItem.IsChecked = settings.WrapMessages;
        StatisticsItem.IsEnabled = log is not null;
        StatisticsItem.IsChecked = log is { StatisticsVisible: true };
        TimeColumnMenu.IsEnabled = log is not null;
        TimeTimestampItem.IsChecked = _timeMode == TimeColumnMode.Timestamp;
        TimeSincePreviousItem.IsChecked = _timeMode == TimeColumnMode.SincePrevious;
        TimeSinceSelectedItem.IsChecked = _timeMode == TimeColumnMode.SinceSelected;
        SyncFormatMenu(log);
    }

    // ---------------------------------------------------------------- format (FR-F1)

    /// <summary>
    /// View ▸ Format: "Detect automatically" (naming what was detected) and every format of the catalog, the one the log in front is read with
    /// ticked; choosing one reads the log again with it. Rebuilt whenever it could change: another tab, a format detected, custom formats edited.
    /// </summary>
    private void SyncFormatMenu(LogViewModel? log)
    {
        FormatMenu.IsEnabled = log is not null;
        FormatMenu.Items.Clear();
        if (log is null)
        {
            return;
        }

        var detect = new RadioMenuFlyoutItem
        {
            Text = log.ChosenFormat is null ? $"Detect automatically ({log.Format.DisplayName})" : "Detect automatically",
            GroupName = "Format",
            IsChecked = log.ChosenFormat is null,
        };
        detect.Click += (_, _) => ReopenWithFormat(log, null);
        FormatMenu.Items.Add(detect);
        FormatMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var format in ViewModel.AvailableFormats)
        {
            var item = new RadioMenuFlyoutItem { Text = format.DisplayName, GroupName = "Format", IsChecked = log.ChosenFormat?.Id == format.Id };
            item.Click += (_, _) => ReopenWithFormat(log, format);
            FormatMenu.Items.Add(item);
        }

        FormatMenu.Items.Add(new MenuFlyoutSeparator());
        var edit = new MenuFlyoutItem { Text = "Custom formats…" };
        ToolTipService.SetToolTip(edit, "Add the output template, layout, pattern or regular expression of your app's text logs (Settings ▸ Formats)");
        edit.Click += OnSettings;
        FormatMenu.Items.Add(edit);
    }

    private async void ReopenWithFormat(LogViewModel log, Core.Formats.ILogFormat? format)
    {
        try
        {
            await ViewModel.ReopenWithFormatAsync(log, format);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Reading the log with another format failed");
            ViewModel.ReportUnexpectedError(ex);
        }

        SyncViewItems();
    }

    private void OnTimelineClick(object sender, RoutedEventArgs e)
    {
        var on = TimelineItem.IsChecked;
        _settings.Update(s => s with { ShowTimeline = on });
    }

    private void OnNewestFirstClick(object sender, RoutedEventArgs e)
    {
        var on = NewestFirstItem.IsChecked;
        _settings.Update(s => s with { NewestFirst = on });
    }

    private void OnWrapClick(object sender, RoutedEventArgs e)
    {
        var on = WrapItem.IsChecked;
        _settings.Update(s => s with { WrapMessages = on });
    }

    private void OnStatisticsClick(object sender, RoutedEventArgs e)
    {
        CurrentView?.ToggleStatistics();
        SyncViewItems();
    }

    private void OnTimeModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TimeColumnMode>(tag, out var mode))
        {
            _timeMode = mode;
            foreach (var (_, view) in _open.Values)
            {
                view.TimeMode = mode;
            }

            SyncViewItems();
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view)
        {
            await view.ExportAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
    }

    private void SyncLiveItems()
    {
        var log = ViewModel.CurrentLog;
        TailItem.IsEnabled = log is { CanFollow: true };
        TailItem.IsChecked = log is { IsFollowing: true };
        PauseItem.IsEnabled = log is { IsFollowing: true };
        PauseItem.IsChecked = log is { IsPaused: true };
        FollowRolledItem.IsEnabled = log is { CanFollow: true };
        FollowRolledItem.IsChecked = log is { FollowRolled: true };
        RefreshItem.IsEnabled = log is { CanRefresh: true };
    }

    private void SyncBookmarkItems()
    {
        var log = ViewModel.CurrentLog;
        BookmarksPanelItem.IsEnabled = log is not null;
        BookmarksPanelItem.IsChecked = log is { BookmarksPanelVisible: true };
        ClearBookmarksItem.IsEnabled = log is { HasBookmarks: true };
    }

    private void OnTailClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentLog is { CanFollow: true } log)
        {
            log.IsFollowing = !log.IsFollowing; // Ctrl+T toggles whatever the menu shows
        }

        SyncLiveItems();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentLog is { IsFollowing: true } log)
        {
            log.IsPaused = !log.IsPaused;
        }

        SyncLiveItems();
    }

    private void OnFollowRolledClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentLog is { } log)
        {
            log.FollowRolled = !log.FollowRolled;
        }

        SyncLiveItems();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.CurrentLog?.Refresh();

    private void OnBookmarksPanelClick(object sender, RoutedEventArgs e)
    {
        CurrentView?.ToggleBookmarksPanel();
        SyncBookmarkItems();
    }

    private void OnToggleBookmark(object sender, RoutedEventArgs e) => CurrentView?.ToggleBookmark();

    private void OnNextBookmark(object sender, RoutedEventArgs e) => CurrentView?.GoToBookmark(forward: true);

    private void OnPreviousBookmark(object sender, RoutedEventArgs e) => CurrentView?.GoToBookmark(forward: false);

    private void OnClearBookmarks(object sender, RoutedEventArgs e) => CurrentView?.ClearBookmarks();

    // ---------------------------------------------------------------- recent files (FR-O1, UX-2)

    private void SyncRecentFiles()
    {
        var files = ViewModel.RecentFiles;
        EmptyState.SetRecentFiles(files);

        RecentMenu.Items.Clear();
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var item = new MenuFlyoutItem { Text = $"{i + 1}  {Path.GetFileName(path)}", Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += OnRecentItemClick;
            RecentMenu.Items.Add(item);
        }

        if (files.Count > 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = "Clear recent files" };
            clear.Click += (_, _) => ViewModel.ClearRecentFiles();
            RecentMenu.Items.Add(clear);
        }
        else
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
        }
    }

    private void OnRecentItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            Open([path]);
        }
    }

    private void OnRecentFileRequested(object? sender, string path) => Open([path]);

    // ---------------------------------------------------------------- commands

    private async void OnOpen(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async void OnEmptyStateOpen(object? sender, EventArgs e) => await PickAndOpenAsync();

    private async void OnOpenFolder(object sender, RoutedEventArgs e) => await PickFolderAndOpenAsync();

    private async void OnEmptyStateOpenFolder(object? sender, EventArgs e) => await PickFolderAndOpenAsync();

    private async void OnMergeFiles(object sender, RoutedEventArgs e) => await PickAndMergeAsync();

    private void OnClose(object sender, RoutedEventArgs e) => ViewModel.Close();

    private void OnCloseOthers(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentLog is { } log)
        {
            ViewModel.CloseOthers(log);
        }
    }

    private void OnCancelLoad(object sender, RoutedEventArgs e) => ViewModel.CancelLoad();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnFind(object sender, RoutedEventArgs e) => CurrentView?.FocusFind();

    private void OnFindNext(object sender, RoutedEventArgs e) => CurrentView?.FindNext(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => CurrentView?.FindNext(forward: false);

    private async void OnFindAll(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view)
        {
            await view.FindAllAsync();
        }
    }

    private void OnFocusFilter(object sender, RoutedEventArgs e) => CurrentView?.FocusQuery();

    private void OnClearFilters(object sender, RoutedEventArgs e) => CurrentView?.ClearFilters();

    private async void OnGoTo(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view)
        {
            await view.GoToAsync();
        }
    }

    private void OnNextProblem(object sender, RoutedEventArgs e) => CurrentView?.GoToProblem(forward: true);

    private void OnPreviousProblem(object sender, RoutedEventArgs e) => CurrentView?.GoToProblem(forward: false);

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

    // ---------------------------------------------------------------- opening (FR-O1 … FR-O4)

    private FileOpenPicker NewFilePicker()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in OpenExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    /// <summary>File ▸ Open: one or more files, each in a tab of its own (FR-O2).</summary>
    private async Task PickAndOpenAsync()
    {
        var files = await NewFilePicker().PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await ViewModel.OpenAsync([.. files.Select(f => f.Path)]);
        }
    }

    /// <summary>File ▸ Merge files: two or more files in one view ordered by time (FR-O4).</summary>
    private async Task PickAndMergeAsync()
    {
        var files = await NewFilePicker().PickMultipleFilesAsync();
        switch (files?.Count ?? 0)
        {
            case 0:
                return;
            case 1:
                ViewModel.Notice = new Notice(NoticeSeverity.Informational, "Choose two or more files", "A merged view combines several files. Select them together in the file dialog (Ctrl+click).");
                return;
            default:
                await ViewModel.OpenMergedAsync([.. files!.Select(f => f.Path)]);
                return;
        }
    }

    /// <summary>File ▸ Open folder (FR-O3).</summary>
    private async Task PickFolderAndOpenAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            await OpenFolderAsync(folder.Path);
        }
    }

    /// <summary>Lists the log files of <paramref name="folder"/> to open in tabs or merge into one view.</summary>
    private async Task OpenFolderAsync(string folder)
    {
        var dialog = new OpenFolderDialog(folder, _folderPatterns) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
        var result = await dialog.ShowAsync();
        _folderPatterns = dialog.Patterns;
        switch (result)
        {
            case ContentDialogResult.Primary:
                await ViewModel.OpenAsync(dialog.SelectedPaths);
                break;
            case ContentDialogResult.Secondary:
                await ViewModel.OpenMergedAsync(dialog.SelectedPaths);
                break;
        }
    }

    // ---------------------------------------------------------------- drag and drop (FR-O1, FR-O3)

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    /// <summary>Dropped files open in tabs; a dropped folder offers its log files.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        List<string> files;
        string? folder;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            files = [.. items.OfType<StorageFile>().Select(f => f.Path)];
            folder = items.OfType<StorageFolder>().FirstOrDefault()?.Path;
        }
        finally
        {
            deferral.Complete();
        }

        if (files.Count > 0)
        {
            await ViewModel.OpenAsync(files);
        }
        else if (folder is not null)
        {
            await OpenFolderAsync(folder);
        }
    }
}
