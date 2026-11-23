using LogLens.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace LogLens.App.Views;

/// <summary>
/// One open log: the filter bar, the find bar, the event grid, the detail pane and the status bar. All state lives in
/// <see cref="LogViewModel"/>; this class only moves focus, reveals rows and copies to the clipboard.
/// </summary>
public sealed partial class LogView : UserControl
{
    private const int MaxFindAllSeedLength = 200;

    private bool _autoSelected;
    private bool _loadedBefore;

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

        // FR-O4: a merged view shows each event's file.
        if (viewModel.IsMerged)
        {
            HeaderSourceColumn.Width = new GridLength(LogFormat.SourceColumnWidth);
            HeaderSourceText.Visibility = Visibility.Visible;
        }

        EventGrid.ShowSource = viewModel.IsMerged;
        EventGrid.IsBookmarked = viewModel.IsBookmarked;
        EventGrid.Source = viewModel.Events;
        EventGrid.AtEndChanged += OnGridAtEndChanged;
        Loaded += (_, _) =>
        {
            Subscribe();
            EventGrid.Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) => Unsubscribe();
    }

    public LogViewModel ViewModel { get; }

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToVisibilityUnless(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;

    private void Subscribe()
    {
        Unsubscribe();
        ViewModel.FilterApplied += OnFilterApplied;
        ViewModel.SearchChanged += OnSearchChanged;
        ViewModel.RowsChanged += OnRowsChanged;
        ViewModel.RowsReset += OnRowsReset;
        ViewModel.ScrollToEndRequested += OnScrollToEndRequested;
        ViewModel.BookmarksChanged += OnBookmarksChanged;

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
        // The list was swapped: keep the event the user was looking at in view if it survived the filters.
        EventGrid.Source = ViewModel.Events;
        var row = previouslyActive is null ? -1 : ViewModel.View.IndexOf(previouslyActive);
        EventGrid.RevealIndex(row >= 0 ? row : 0);
    }

    private void OnRowsChanged(object? sender, RowsChange change)
    {
        EventGrid.RowsChanged(change.FirstRow);

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

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
