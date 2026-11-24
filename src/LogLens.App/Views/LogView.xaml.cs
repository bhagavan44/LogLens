using System.ComponentModel;
using System.Globalization;
using LogLens.Core.Analysis;
using LogLens.Core.Collections;
using LogLens.Core.Export;
using LogLens.Core.Models;
using LogLens.Core.Settings;
using LogLens.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace LogLens.App.Views;

/// <summary>
/// One open log: the filter bar, the find bar, the timeline, the event grid, the detail pane and the status bar. All state lives in
/// <see cref="LogViewModel"/> and <see cref="SettingsService"/>; this class only moves focus, reveals rows, lays out the columns and copies
/// to the clipboard.
/// </summary>
public sealed partial class LogView : UserControl
{
    private const int MaxFindAllSeedLength = 200;

    private readonly SettingsService _settings;
    private AppSettings _applied = new();
    private ColumnLayout _columns = new();
    private GridMetrics _metrics = GridMetrics.Default;
    private double _gripStartWidth;
    private ExportFormat _lastExportFormat = ExportFormat.Csv;
    private bool _autoSelected;
    private bool _loadedBefore;

    public LogView(LogViewModel viewModel, SettingsService settings)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();

        ExceptionBox.FontFamily = LogFormat.CodeFont;
        RawBox.FontFamily = LogFormat.CodeFont;

        EventGrid.IsBookmarked = viewModel.IsBookmarked;
        EventGrid.Source = viewModel.Events;
        EventGrid.AtEndChanged += OnGridAtEndChanged;
        ApplySettings(settings.Current);
        Loaded += (_, _) =>
        {
            Subscribe();
            EventGrid.Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) => Unsubscribe();
    }

    public LogViewModel ViewModel { get; }

    /// <summary>The query bar, where the first-run hint points (UX-7).</summary>
    public FrameworkElement FilterTarget => QueryBox;

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToVisibilityUnless(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"12,345", for the count of a statistics row.</summary>
    public static string FormatCount(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What a screen reader says for a statistics row.</summary>
    public static string DescribeGroup(int count, string key) => string.Create(CultureInfo.InvariantCulture, $"{count:N0} times: {key}");

    private void Subscribe()
    {
        Unsubscribe();
        ViewModel.FilterApplied += OnFilterApplied;
        ViewModel.SearchChanged += OnSearchChanged;
        ViewModel.RowsChanged += OnRowsChanged;
        ViewModel.RowsReset += OnRowsReset;
        ViewModel.ScrollToEndRequested += OnScrollToEndRequested;
        ViewModel.BookmarksChanged += OnBookmarksChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The order, the layout and the rows may have changed while this tab was in the background.
        SyncOrder();
        ApplyLayout();

        // Rows that arrived before the view was loaded, or while its tab was in the background (where a tail kept following the end).
        var followLatest = ViewModel.FollowLatest;
        EventGrid.Highlighter = ViewModel.Highlighter;
        OnRowsChanged(ViewModel, new RowsChange(0, 0, ViewModel.Events.Count));
        if (_loadedBefore && followLatest)
        {
            EventGrid.ScrollToEnd();
        }

        _loadedBefore = true;
        ViewModel.FollowLatest = EventGrid.IsAtEnd;
    }

    private void Unsubscribe()
    {
        ViewModel.FilterApplied -= OnFilterApplied;
        ViewModel.SearchChanged -= OnSearchChanged;
        ViewModel.RowsChanged -= OnRowsChanged;
        ViewModel.RowsReset -= OnRowsReset;
        ViewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        ViewModel.BookmarksChanged -= OnBookmarksChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogViewModel.IsMerged) or nameof(LogViewModel.HasSourceContext))
        {
            ApplyLayout(); // a merged view gets its Source column once the files are known, a structured log its Context column once detected
        }
        else if (e.PropertyName == nameof(LogViewModel.HasStructuredFields) && !ViewModel.HasStructuredFields && DetailTabs.SelectedItem == PropertiesTab)
        {
            DetailTabs.SelectedItem = MessageTab;
        }
        else if (e.PropertyName == nameof(LogViewModel.NewestFirst))
        {
            UpdateTimeHeader();
        }
    }

    // ------------------------------------------------------------------ view options (settings, FR-G1, FR-G3, FR-G6)

    /// <summary>
    /// Takes the settings a tab shows: the row order, wrapped messages, the timeline, the column layout, the log font and row density (FR-P2),
    /// how timestamps are shown (FR-G5, FR-P3) and the hide rules (FR-S6).
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applied = settings;
        _columns = settings.Columns;
        ViewModel.TimelineVisible = settings.ShowTimeline;
        ViewModel.NewestFirst = settings.NewestFirst;
        ViewModel.SetTimeDisplay(settings.ToTimeDisplay());
        ViewModel.SetHideRules(settings.HideRules);

        var font = LogFormat.TextFont(settings.FontFamily);
        EventGrid.TextFont = font;
        EventGrid.TimeDisplay = ViewModel.TimeDisplay;
        MessageBox.FontFamily = font;
        foreach (var box in new[] { MessageBox, ExceptionBox, RawBox })
        {
            box.FontSize = settings.FontSize;
        }

        SyncOrder();
        ApplyLayout();
    }

    /// <summary>What the Time column shows (View ▸ Time column, FR-G5).</summary>
    public TimeColumnMode TimeMode
    {
        get;
        set
        {
            field = value;
            EventGrid.TimeMode = value;
            UpdateTimeHeader();
        }
    }

    /// <summary>The grid reads the list in the view-model's order; a tab that was in the background when the order changed catches up here.</summary>
    private void SyncOrder()
    {
        if (EventGrid.NewestFirst != ViewModel.NewestFirst)
        {
            OnFilterApplied(ViewModel, ViewModel.SelectedEvent);
        }

        UpdateTimeHeader();
    }

    /// <summary>The Time column's header says what the cells hold, with a down arrow while the newest event is listed first.</summary>
    private void UpdateTimeHeader() => TimeHeaderText.Text = TimeMode switch
    {
        TimeColumnMode.SincePrevious => "Since previous",
        TimeColumnMode.SinceSelected => "Since selected",
        _ => "Time",
    } + (ViewModel.NewestFirst ? " ↓" : string.Empty);

    /// <summary>Sizes the header and the rows to the column layout and the wrap option; a hidden column is 0 wide.</summary>
    private void ApplyLayout()
    {
        _metrics = GridMetrics.For(
            _columns,
            _applied.WrapMessages,
            ViewModel.IsMerged,
            _applied.FontSize,
            _applied.RowDensity,
            _applied.TimestampFormat,
            hasContext: ViewModel.HasSourceContext);
        HeaderTimeColumn.Width = new GridLength(_metrics.TimeColumnWidth);
        HeaderLevelColumn.Width = new GridLength(_metrics.LevelColumnWidth);
        HeaderSourceColumn.Width = new GridLength(_metrics.SourceColumnWidth);
        HeaderContextColumn.Width = new GridLength(_metrics.ContextColumnWidth);
        TimeHeaderText.Visibility = TimeGrip.Visibility = ToVisibility(_metrics.TimeColumnWidth > 0);
        LevelHeaderText.Visibility = ToVisibility(_metrics.LevelColumnWidth > 0);
        SourceHeaderText.Visibility = SourceGrip.Visibility = ToVisibility(_metrics.SourceColumnWidth > 0);
        ContextHeaderText.Visibility = ContextGrip.Visibility = ToVisibility(_metrics.ContextColumnWidth > 0);
        EventGrid.Metrics = _metrics;
    }

    // ------------------------------------------------------------------ columns (FR-G1)

    private void OnColumnMenuOpening(object? sender, object e)
    {
        ColumnTimeItem.IsChecked = _columns.ShowTime;
        ColumnLevelItem.IsChecked = _columns.ShowLevel;
        ColumnSourceItem.IsChecked = _columns.ShowSource;
        ColumnSourceItem.IsEnabled = ViewModel.IsMerged;
        ColumnContextItem.IsChecked = _columns.ShowContext;
        ColumnContextItem.IsEnabled = ViewModel.HasSourceContext;
    }

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
        => CommitColumns(_columns with
        {
            ShowTime = ColumnTimeItem.IsChecked,
            ShowLevel = ColumnLevelItem.IsChecked,
            ShowSource = ColumnSourceItem.IsChecked,
            ShowContext = ColumnContextItem.IsChecked,
        });

    private void OnColumnResetClick(object sender, RoutedEventArgs e) => CommitColumns(_columns with { TimeWidth = null, SourceWidth = null, ContextWidth = null });

    private double GripWidth(object? grip)
        => ReferenceEquals(grip, TimeGrip) ? _metrics.TimeColumnWidth
            : ReferenceEquals(grip, ContextGrip) ? _metrics.ContextColumnWidth
            : _metrics.SourceColumnWidth;

    private ColumnLayout WithGripWidth(object? grip, double? width)
        => ReferenceEquals(grip, TimeGrip) ? _columns with { TimeWidth = width }
            : ReferenceEquals(grip, ContextGrip) ? _columns with { ContextWidth = width }
            : _columns with { SourceWidth = width };

    private void OnGripDragStarted(object? sender, EventArgs e) => _gripStartWidth = GripWidth(sender);

    // While a header edge is dragged only this tab follows; the layout is saved (and every tab updated) when the drag ends.
    private void OnGripDragDelta(object? sender, double delta) => PreviewColumns(WithGripWidth(sender, _gripStartWidth + delta));

    private void OnGripDragCompleted(object? sender, EventArgs e) => CommitColumns(_columns);

    private void OnGripNudged(object? sender, double delta) => CommitColumns(WithGripWidth(sender, GripWidth(sender) + delta));

    private void OnGripReset(object? sender, EventArgs e) => CommitColumns(WithGripWidth(sender, null));

    private void PreviewColumns(ColumnLayout layout)
    {
        _columns = layout.Normalised();
        ApplyLayout();
    }

    private void CommitColumns(ColumnLayout layout)
    {
        PreviewColumns(layout);
        _settings.Update(s => s with { Columns = _columns });
    }

    // ------------------------------------------------------------------ saved filters (FR-S7)

    /// <summary>Fills the Saved menu: one item per saved filter (ticked when it is in force), then Save and Delete.</summary>
    private void OnSavedFiltersMenuOpening(object? sender, object e)
    {
        var menu = SavedFiltersMenu.Items;
        menu.Clear();
        var saved = _settings.Current.SavedFilters;
        foreach (var filter in saved)
        {
            var item = new ToggleMenuFlyoutItem { Text = filter.Name, IsChecked = ViewModel.Matches(filter) };
            ToolTipService.SetToolTip(item, DescribeFilter(filter));
            item.Click += (_, _) =>
            {
                ViewModel.ApplySavedFilter(filter);
                EventGrid.Focus(FocusState.Programmatic);
            };
            menu.Add(item);
        }

        if (saved.Count == 0)
        {
            menu.Add(new MenuFlyoutItem { Text = "No saved filters yet", IsEnabled = false });
        }

        menu.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "Save current filter…", Icon = new FontIcon { Glyph = "\uE74E" } };
        save.Click += async (_, _) => await SaveCurrentFilterAsync();
        menu.Add(save);

        if (saved.Count > 0)
        {
            var delete = new MenuFlyoutSubItem { Text = "Delete", Icon = new FontIcon { Glyph = "\uE74D" } };
            foreach (var filter in saved)
            {
                var item = new MenuFlyoutItem { Text = filter.Name };
                item.Click += (_, _) => _settings.Update(s => s with { SavedFilters = [.. s.SavedFilters.Where(f => f.Name != filter.Name)] });
                delete.Items.Add(item);
            }

            menu.Add(delete);
        }
    }

    /// <summary>"Levels: Warning, Error · @m like '%payment%'": what a saved filter sets, for its tooltip.</summary>
    private string DescribeFilter(SavedFilter filter)
    {
        var levels = ViewModel.LevelToggles.Where(t => Core.Filtering.LevelMask.Allows(filter.Levels, t.Level)).Select(t => t.Label).ToList();
        var levelText = levels.Count == ViewModel.LevelToggles.Count ? "All levels" : levels.Count == 0 ? "No levels" : "Levels: " + string.Join(", ", levels);
        return string.IsNullOrWhiteSpace(filter.Query) ? levelText : $"{levelText}\n{filter.Query}";
    }

    /// <summary>Asks for a name and saves the query and level buttons under it; a filter with that name is replaced.</summary>
    private async Task SaveCurrentFilterAsync()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Payment errors", MaxLength = 80 };
        AutomationProperties.SetName(name, "Saved filter name");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Save filter",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Saves the query and the level buttons as they are now. Pick it again from the Saved menu in any log.", TextWrapping = TextWrapping.Wrap },
                    name,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text))
        {
            var filter = ViewModel.CaptureFilter(name.Text);
            _settings.Update(s => s with
            {
                SavedFilters = [.. s.SavedFilters.Where(f => !string.Equals(f.Name, filter.Name, StringComparison.OrdinalIgnoreCase)), filter],
            });
        }

        EventGrid.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------ timeline (FR-N2) and statistics (FR-X1, FR-X2)

    private void OnTimelineRangeSelected(object? sender, (DateTime From, DateTime To) range) => ViewModel.SelectTimeRange(range.From, range.To);

    private void OnTimelineClearRequested(object? sender, EventArgs e) => ViewModel.ClearTime();

    /// <summary>View ▸ Statistics: shows the statistics in the detail pane, or goes back to the message.</summary>
    public void ToggleStatistics() => DetailTabs.SelectedItem = ViewModel.StatisticsVisible ? MessageTab : StatisticsTab;

    private void OnTopMessageClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StatGroup group)
        {
            ViewModel.ShowOnlyMessage(group);
        }
    }

    private void OnTopExceptionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StatGroup group)
        {
            ViewModel.ShowOnlyException(group);
        }
    }

    // ------------------------------------------------------------------ export (FR-E1)

    /// <summary>File ▸ Export: asks for a format and what to export, then for a file, and writes it.</summary>
    public async Task ExportAsync(IntPtr windowHandle)
    {
        var selected = EventGrid.SelectedEvents().ToList();
        var dialog = new ExportDialog(ViewModel.ExportableCount(), selected.Count, _lastExportFormat) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            EventGrid.Focus(FocusState.Programmatic);
            return;
        }

        var format = _lastExportFormat = dialog.Format;
        var selectionOnly = dialog.SelectionOnly;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SuggestedExportName(format, selectionOnly)),
        };
        picker.FileTypeChoices.Add(LogExporter.DisplayName(format), [LogExporter.FileExtension(format)]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        if (await picker.PickSaveFileAsync() is { } file)
        {
            try
            {
                await using var stream = await file.OpenStreamForWriteAsync();
                stream.SetLength(0); // overwriting an existing file
                var written = await ViewModel.ExportAsync(format, stream, selectionOnly ? selected : null);
                ViewModel.ReportExported(file.Path, written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ViewModel.ReportExportFailed(file.Path, ex);
            }
        }

        EventGrid.Focus(FocusState.Programmatic);
    }

    private async void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
    {
        var events = EventGrid.SelectedEvents().ToList();
        if (events.Count > 0)
        {
            SetClipboard(await ViewModel.BuildMarkdownAsync(events));
        }
    }

    // ------------------------------------------------------------------ commands (menu and shortcuts)

    /// <summary>Ctrl+F: shows the find bar and puts the cursor in it.</summary>
    public void FocusFind()
    {
        ViewModel.FindBarVisible = true;

        // A bar that was collapsed a moment ago can't take focus until it has been laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
        });
    }

    /// <summary>F3 / Shift+F3: the next / previous match; opens the find bar first when it is closed.</summary>
    public void FindNext(bool forward)
    {
        if (!ViewModel.FindBarVisible)
        {
            FocusFind();
            return;
        }

        EventGrid.RevealIndex(ViewModel.StepMatch(EventGrid.ActiveIndex, forward));
    }

    /// <summary>Ctrl+L: puts the cursor in the query bar.</summary>
    public void FocusQuery()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    /// <summary>Ctrl+E / Ctrl+Shift+E: the next / previous warning, error or fatal event (FR-N3).</summary>
    public void GoToProblem(bool forward) => EventGrid.RevealIndex(ViewModel.NextProblemRow(EventGrid.ActiveIndex, forward));

    /// <summary>Ctrl+G: jumps to an event number or a time (FR-G7).</summary>
    public async Task GoToAsync()
    {
        var dialog = new GoToDialog(ViewModel.ResolveGoTo) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.TargetRow >= 0)
        {
            EventGrid.RevealIndex(dialog.TargetRow);
            EventGrid.Focus(FocusState.Programmatic);
        }
    }

    public void ClearFilters()
    {
        ViewModel.ClearFilters();
        ContextBox.Value = 0;
    }

    // ------------------------------------------------------------------ bookmarks (FR-N1)

    /// <summary>Ctrl+F2: bookmarks the active event, or removes its bookmark.</summary>
    public void ToggleBookmark()
    {
        if (EventGrid.ActiveEvent is { } active)
        {
            ViewModel.ToggleBookmark(active);
        }
    }

    /// <summary>F2 / Shift+F2: the next / previous bookmark the filters leave in the list.</summary>
    public void GoToBookmark(bool forward)
    {
        var row = ViewModel.NextBookmarkRow(EventGrid.ActiveIndex, forward);
        if (row >= 0)
        {
            EventGrid.RevealIndex(row);
            BookmarkHint.Visibility = Visibility.Collapsed;
        }
        else if (ViewModel.HasBookmarks)
        {
            ShowBookmarkHint("The bookmarked events are hidden by the current filters.");
        }
    }

    public void ClearBookmarks() => ViewModel.ClearBookmarks();

    /// <summary>View ▸ Bookmarks: shows or hides the bookmarks panel.</summary>
    public void ToggleBookmarksPanel() => ViewModel.BookmarksPanelVisible = !ViewModel.BookmarksPanelVisible;

    private void OnBookmarksChanged(object? sender, EventArgs e)
    {
        EventGrid.Refresh();
        BookmarkHint.Visibility = Visibility.Collapsed;
    }

    private void ShowBookmarkHint(string text)
    {
        BookmarkHint.Text = text;
        BookmarkHint.Visibility = Visibility.Visible;
        ViewModel.BookmarksPanelVisible = true;
    }

    private void OnBookmarkItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BookmarkItem item)
        {
            return;
        }

        var row = ViewModel.RowOf(item);
        if (row < 0)
        {
            ShowBookmarkHint("This event is hidden by the current filters. Clear the filters to see it.");
            return;
        }

        BookmarkHint.Visibility = Visibility.Collapsed;
        EventGrid.RevealIndex(row);
        EventGrid.Focus(FocusState.Programmatic);
    }

    private void OnRemoveBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BookmarkItem item })
        {
            ViewModel.RemoveBookmark(item);
        }
    }

    private void OnClearBookmarksClick(object sender, RoutedEventArgs e) => ClearBookmarks();

    private void OnCloseBookmarksPanelClick(object sender, RoutedEventArgs e)
    {
        ViewModel.BookmarksPanelVisible = false;
        EventGrid.Focus(FocusState.Programmatic);
    }

    private void OnRowBookmarkClick(object sender, RoutedEventArgs e) => ToggleBookmark();

    // ------------------------------------------------------------------ find all events containing (FR-N4)

    /// <summary>
    /// Ctrl+Shift+F: asks what to look for (filled in from the text selected in the detail pane, or the active event's message) and lists every
    /// event containing it, across every file of a merged view.
    /// </summary>
    public async Task FindAllAsync()
    {
        var seed = SelectedDetailText() ?? (EventGrid.ActiveEvent is { IsUnparsed: false } active ? Core.Models.LogText.FirstLine(active.Message) : string.Empty);
        if (seed.Length > MaxFindAllSeedLength)
        {
            seed = seed[..MaxFindAllSeedLength];
        }

        var dialog = new FindAllDialog(seed, ViewModel.ContextEvents, ViewModel.IsMerged) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            ViewModel.FindAllContaining(dialog.Text, dialog.ContextEvents, dialog.ClearOtherFilters);
            ContextBox.Value = ViewModel.ContextEvents;
        }

        EventGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>The first line of the text selected in the detail pane, if any.</summary>
    private string? SelectedDetailText()
    {
        foreach (var box in new[] { MessageBox, ExceptionBox, RawBox })
        {
            if (box.Visibility == Visibility.Visible && box.SelectedText?.Trim() is { Length: > 0 } selected)
            {
                return Core.Models.LogText.FirstLine(selected).Trim();
            }
        }

        return null;
    }

    private async void OnRowFindAllClick(object sender, RoutedEventArgs e) => await FindAllAsync();

    // ------------------------------------------------------------------ view-model → grid

    private void OnFilterApplied(object? sender, Core.Models.LogEvent? previouslyActive)
    {
        // The list was swapped (or turned round): keep the event the user was looking at in view if it survived the filters.
        EventGrid.NewestFirst = ViewModel.NewestFirst;
        EventGrid.Source = ViewModel.Events;
        var row = previouslyActive is null ? -1 : ViewModel.View.IndexOf(previouslyActive);
        EventGrid.RevealIndex(row >= 0 ? row : 0);
    }

    private void OnRowsChanged(object? sender, RowsChange change)
    {
        EventGrid.RowsChanged(change.FirstRow, change.Removed, change.Added);

        // The first rows of a file that is still loading: select the first one so the detail pane has something to show.
        if (EventGrid.ActiveIndex < 0 && EventGrid.RowCount > 0 && !_autoSelected)
        {
            _autoSelected = true;
            EventGrid.RevealIndex(0);
        }
    }

    private void OnRowsReset(object? sender, EventArgs e)
    {
        _autoSelected = false;
        EventGrid.Source = ViewModel.Events;
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => EventGrid.ScrollToEnd();

    private void OnGridAtEndChanged(object? sender, bool atEnd) => ViewModel.FollowLatest = atEnd;

    private void OnJumpToLatestClick(object sender, RoutedEventArgs e)
    {
        ViewModel.JumpToLatest();
        EventGrid.Focus(FocusState.Programmatic);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnStopLoadingClick(object sender, RoutedEventArgs e) => ViewModel.StopLoading();

    private void OnSearchChanged(object? sender, EventArgs e)
    {
        EventGrid.Highlighter = ViewModel.Highlighter;
        EventGrid.Refresh();
    }

    private void OnGridSelectionChanged(object? sender, EventArgs e)
    {
        ViewModel.SelectedEvent = EventGrid.ActiveEvent;
        ViewModel.UpdateActiveRow(EventGrid.ActiveIndex);
    }

    // ------------------------------------------------------------------ filter bar

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ViewModel.ApplyQueryNow();
            EventGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.QueryText.Length > 0)
        {
            ViewModel.QueryText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e) => ClearFilters();

    private void OnClearPinClick(object sender, RoutedEventArgs e) => ViewModel.ClearPin();

    private void OnClearHiddenGroupsClick(object sender, RoutedEventArgs e) => ViewModel.ClearHiddenGroups();

    private void OnLastClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string seconds } && double.TryParse(seconds, out var s))
        {
            ViewModel.SetLast(TimeSpan.FromSeconds(s));
            TimeButton.Flyout.Hide();
        }
    }

    private void OnAroundClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string seconds } && double.TryParse(seconds, out var s) && ViewModel.SetAround(TimeSpan.FromSeconds(s)))
        {
            TimeButton.Flyout.Hide();
        }
    }

    private void OnApplyTimeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ApplyTimeBoxes())
        {
            TimeButton.Flyout.Hide();
        }
    }

    private void OnClearTimeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearTime();
        TimeButton.Flyout.Hide();
    }

    private void OnTimeBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OnApplyTimeClick(sender, e);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ find bar

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                FindNext(forward: !IsDown(VirtualKey.Shift));
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext(forward: true);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => FindNext(forward: false);

    private void OnCloseFindClick(object sender, RoutedEventArgs e) => CloseFind();

    private void OnContextChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => ViewModel.ContextEvents = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;

    private void CloseFind()
    {
        ViewModel.FindBarVisible = false;
        EventGrid.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------ row context menu (FR-S5)

    private void OnRowMenuOpening(object? sender, object e)
    {
        var active = EventGrid.ActiveEvent;
        var parsed = active is { IsUnparsed: false };
        RowShowLikeItem.IsEnabled = RowHideLikeItem.IsEnabled = RowAroundItem.IsEnabled = parsed;
        RowOnlyLevelItem.IsEnabled = RowHideLevelItem.IsEnabled = active is not null && ViewModel.CanFilterByLevel(active.Level);
        RowBookmarkItem.IsEnabled = active is not null;
        RowBookmarkItem.Text = active is not null && ViewModel.IsBookmarked(active) ? "Remove bookmark" : "Bookmark";
    }

    private void OnRowShowLikeClick(object sender, RoutedEventArgs e)
    {
        if (EventGrid.ActiveEvent is { } active)
        {
            ViewModel.ShowOnlyLike(active);
        }
    }

    private void OnRowHideLikeClick(object sender, RoutedEventArgs e)
    {
        if (EventGrid.ActiveEvent is { } active)
        {
            ViewModel.HideLike(active);
        }
    }

    private void OnRowOnlyLevelClick(object sender, RoutedEventArgs e)
    {
        if (EventGrid.ActiveEvent is { } active)
        {
            ViewModel.ShowOnlyLevel(active.Level);
        }
    }

    private void OnRowHideLevelClick(object sender, RoutedEventArgs e)
    {
        if (EventGrid.ActiveEvent is { } active)
        {
            ViewModel.HideLevel(active.Level);
        }
    }

    private void OnRowAroundClick(object sender, RoutedEventArgs e) => ViewModel.SetAround(TimeSpan.FromSeconds(30));

    // ------------------------------------------------------------------ detail pane and clipboard

    private void OnDetailTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        MessageBox.Visibility = selected == MessageTab ? Visibility.Visible : Visibility.Collapsed;
        ExceptionBox.Visibility = selected == ExceptionTab ? Visibility.Visible : Visibility.Collapsed;
        RawBox.Visibility = selected == RawTab ? Visibility.Visible : Visibility.Collapsed;
        PropertiesPanel.Visibility = selected == PropertiesTab ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility = selected == StatisticsTab ? Visibility.Visible : Visibility.Collapsed;
        CopyExceptionButton.Visibility = ExceptionBox.Visibility;

        // The statistics are only computed (and kept up to date) while they are showing.
        ViewModel.StatisticsVisible = selected == StatisticsTab;
    }

    // ----- properties (FR-D5) -----

    /// <summary>What a screen reader hears for a row of the properties table.</summary>
    public static string DescribeProperty(string name, string value) => $"{name}: {value}";

    private static PropertyRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as PropertyRow;

    private void OnPropertyFilterClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.FilterToProperty(row, exclude: false);
        }
    }

    private void OnPropertyExcludeClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.FilterToProperty(row, exclude: true);
        }
    }

    private void OnPropertyCopyClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            SetClipboard(row.ValueText);
        }
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

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
