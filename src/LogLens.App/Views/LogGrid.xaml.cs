using LogLens.Core.Collections;
using LogLens.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace LogLens.App.Views;

/// <summary>
/// A virtualised, read-only log grid (FR-G1). It keeps a small pool of <see cref="LogRow"/> visuals — one per visible row — and re-binds them
/// as the user scrolls, with a <see cref="ScrollBar"/> standing in for the whole list.
/// </summary>
/// <remarks>
/// Not a <c>ListView</c>: its scroll extent (rows × row height × DPI scale) overflows an internal limit at about 127 k rows at 150 % scaling,
/// after which rows paint outside the control. Scroll position and selection live in <see cref="GridViewport"/> and
/// <see cref="SelectionModel"/> (unit-tested in Core), so row counts are unbounded. Every row has the same height.
/// </remarks>
public sealed partial class LogGrid : UserControl
{
    private readonly List<LogRow> _pool = [];
    private readonly GridViewport _viewport = new();
    private readonly SelectionModel _selection = new();
    private IReadOnlyList<LogEvent>? _source;
    private GridMetrics _metrics = GridMetrics.Default;
    private Windows.Foundation.Size _hostSize;
    private LogEvent? _lastActive;
    private bool _syncingScrollBar;

    public LogGrid()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Row height and column widths (FR-G1, FR-G6). Every row has this one height, also when long messages wrap, so the grid still counts rows.
    /// Selection and scroll position are kept.
    /// </summary>
    public GridMetrics Metrics
    {
        get => _metrics;
        set
        {
            if (_metrics == value)
            {
                return;
            }

            _metrics = value;
            if (_hostSize.Height > 0)
            {
                Layout(_hostSize);
            }
        }
    }

    /// <summary>The font of the log text (FR-P2).</summary>
    public FontFamily TextFont
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                foreach (var row in _pool)
                {
                    row.SetFont(value);
                }
            }
        }
    } = LogFormat.DefaultTextFont;

    /// <summary>The zone and format of the timestamps (FR-G5, FR-P3).</summary>
    public TimeDisplay TimeDisplay
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Render();
            }
        }
    } = TimeDisplay.Default;

    /// <summary>The rows run from the newest event down (FR-G3): new events arrive at the top. Set before the source is replaced.</summary>
    public bool NewestFirst { get; set; }

    /// <summary>What the Time column shows (FR-G5).</summary>
    public TimeColumnMode TimeMode
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Render();
            }
        }
    }

    /// <summary>Raised when the active row or the set of selected rows changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised for Ctrl+C (<c>raw: false</c>) and Ctrl+Shift+C (<c>raw: true</c>), FR-G4.</summary>
    public event EventHandler<bool>? CopyRequested;

    /// <summary>The events to show. Setting it resets the selection and scrolls to the top.</summary>
    public IReadOnlyList<LogEvent>? Source
    {
        get => _source;
        set
        {
            _source = value;
            var count = _source?.Count ?? 0;
            _selection.Reset(count);
            _viewport.Configure(count, _viewport.VisibleRows);
            _viewport.ScrollTo(0);
            Render();
            RaiseSelectionChanged();
        }
    }

    /// <summary>The row with keyboard focus (what the detail pane shows), or null.</summary>
    public LogEvent? ActiveEvent => _source is { } s && _selection.Active >= 0 && _selection.Active < s.Count ? s[_selection.Active] : null;

    /// <summary>Index of the active row; -1 when there is none.</summary>
    public int ActiveIndex => _selection.Active;

    public int SelectedCount => _selection.Count;

    /// <summary>Returns the ranges of a row's message to highlight (find bar, FR-S3); null for none. Call <see cref="Refresh"/> after changing it.</summary>
    public Func<string, IReadOnlyList<(int Start, int Length)>>? Highlighter { get; set; }

    /// <summary>Says whether an event is bookmarked, for the row marker (FR-N1). Call <see cref="Refresh"/> after the bookmarks changed.</summary>
    public Func<LogEvent, bool>? IsBookmarked { get; set; }

    /// <summary>Re-renders the visible rows; selection and scroll position are kept.</summary>
    public void Refresh() => Render();

    /// <summary>
    /// The source changed (rows appended, or the last ones replaced, while a log loads or is tailed): the scroll position and the selection
    /// are kept. Oldest first the change is at <paramref name="firstRow"/>; newest first it is at the top, where <paramref name="removed"/>
    /// rows went and <paramref name="added"/> came, and every other row moved down with its event.
    /// </summary>
    public void RowsChanged(int firstRow, int removed, int added)
    {
        var count = RowCount;
        if (NewestFirst)
        {
            // The selection moves with its events; the rows on screen stay put unless the newest event is showing, in which case the
            // new ones come into view.
            var inserted = count - _selection.ItemCount;
            var atLatest = _viewport.First == 0;
            if (inserted > 0)
            {
                _selection.InsertAtStart(inserted);
            }
            else
            {
                _selection.Resize(count);
            }

            _viewport.Configure(count, _viewport.VisibleRows);
            if (inserted > 0 && !atLatest)
            {
                _viewport.ScrollBy(inserted);
            }

            Render();
            if (!ReferenceEquals(ActiveEvent, _lastActive))
            {
                RaiseSelectionChanged(); // the active event object was replaced: the detail pane re-reads it
            }

            return;
        }

        var activeAffected = _selection.Active >= firstRow;
        _selection.Resize(count);
        _viewport.Configure(count, _viewport.VisibleRows);
        Render();
        if (activeAffected)
        {
            RaiseSelectionChanged(); // the active row now shows another (or a replaced) event: the detail pane re-reads it
        }
    }

    /// <summary>The newest event's row is in view (the last row, or the first when newest first), so new events show on screen (FR-T2).</summary>
    public bool IsAtEnd { get; private set; } = true;

    /// <summary>Raised when the view moves to or away from the newest event (scrolling, or the list growing).</summary>
    public event EventHandler<bool>? AtEndChanged;

    /// <summary>Scrolls so the newest event is in view (the last row, or the first when newest first); the selection stays.</summary>
    public void ScrollToEnd()
    {
        _viewport.ScrollTo(NewestFirst ? 0 : _viewport.MaxFirst);
        Render();
    }

    /// <summary>Selects row <paramref name="index"/> and scrolls it into view, centred when it was off-screen.</summary>
    public void RevealIndex(int index)
    {
        if (_source is null || index < 0 || index >= _source.Count)
        {
            return;
        }

        _selection.SelectSingle(index);
        if (index < _viewport.First || index >= _viewport.First + _viewport.VisibleRows)
        {
            _viewport.Center(index);
        }

        Render();
        RaiseSelectionChanged();
    }

    public IEnumerable<LogEvent> SelectedEvents()
    {
        if (_source is null)
        {
            yield break;
        }

        foreach (var index in _selection.SelectedIndices())
        {
            yield return _source[index];
        }
    }

    /// <summary>Tells listeners (the detail pane) and screen readers that the active row or the selection changed.</summary>
    private void RaiseSelectionChanged()
    {
        _lastActive = ActiveEvent;
        SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Keyboard focus stays on the grid itself; announce the active row as the focused item so Narrator reads it.
        if (FocusState != FocusState.Unfocused
            && ActiveRowVisual() is { } row
            && AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged)
            && FrameworkElementAutomationPeer.CreatePeerForElement(row) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }
    }

    // ------------------------------------------------------------------ UI Automation (see LogGridAutomation.cs)

    protected override AutomationPeer OnCreateAutomationPeer() => new LogGridAutomationPeer(this);

    internal IEnumerable<LogRow> VisibleRows() => _pool.Where(r => r.Visibility == Visibility.Visible);

    internal LogRow? ActiveRowVisual()
    {
        var offset = _selection.Active - _viewport.First;
        return offset >= 0 && offset < _pool.Count && _pool[offset].Visibility == Visibility.Visible ? _pool[offset] : null;
    }

    internal bool IsRowSelected(int index) => index >= 0 && index < RowCount && _selection.IsSelected(index);

    internal int RowCount => _source?.Count ?? 0;

    internal int FirstRow => _viewport.First;

    internal int MaxFirstRow => _viewport.MaxFirst;

    internal int VisibleRowCount => _viewport.VisibleRows;

    internal void ScrollRows(int rows)
    {
        if (_viewport.ScrollBy(rows))
        {
            Render();
        }
    }

    internal void ScrollToRow(int first)
    {
        if (_viewport.ScrollTo(first))
        {
            Render();
        }
    }

    internal void SelectRowFromAutomation(int index, SelectionChange change)
    {
        if (index < 0 || index >= RowCount)
        {
            return;
        }

        switch (change)
        {
            case SelectionChange.Single:
                _selection.SelectSingle(index);
                break;
            case SelectionChange.Add when !_selection.IsSelected(index):
            case SelectionChange.Remove when _selection.IsSelected(index):
                _selection.Toggle(index);
                break;
        }

        _viewport.EnsureVisible(index);
        Render();
        RaiseSelectionChanged();
    }

    // ------------------------------------------------------------------ layout

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => Layout(e.NewSize);

    private void Layout(Windows.Foundation.Size size)
    {
        _hostSize = size;
        RowsHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height) };

        var rowHeight = _metrics.RowHeight;
        var visible = Math.Max(1, (int)(size.Height / rowHeight));
        _viewport.Configure(RowCount, visible);

        // One extra row so a partially visible row at the bottom is drawn too.
        var needed = visible + 1;
        while (_pool.Count < needed)
        {
            var row = new LogRow { Owner = this };
            row.SetFont(TextFont);
            _pool.Add(row);
            RowsHost.Children.Add(row);
        }

        while (_pool.Count > needed)
        {
            RowsHost.Children.Remove(_pool[^1]);
            _pool.RemoveAt(_pool.Count - 1);
        }

        for (var i = 0; i < _pool.Count; i++)
        {
            _pool[i].Width = size.Width;
            _pool[i].Apply(_metrics);
            Canvas.SetTop(_pool[i], i * rowHeight);
        }

        Render();
    }

    private void Render()
    {
        var count = RowCount;
        var focused = FocusState != FocusState.Unfocused;
        var mode = TimeMode;
        var display = TimeDisplay;
        var selectedReference = mode == TimeColumnMode.SinceSelected ? ActiveEvent : null;

        // "Since previous" measures from the event before in time: the row above, or the row below when the newest event is first.
        var previousOffset = NewestFirst ? 1 : -1;
        for (var i = 0; i < _pool.Count; i++)
        {
            var index = _viewport.First + i;
            var row = _pool[i];
            if (_source is not null && index < count)
            {
                row.Index = index;
                AutomationProperties.SetPositionInSet(row, index + 1);
                AutomationProperties.SetSizeOfSet(row, count);
                row.Visibility = Visibility.Visible;
                var e = _source[index];
                var previous = index + previousOffset;
                var reference = mode == TimeColumnMode.SincePrevious
                    ? (previous >= 0 && previous < count ? _source[previous] : null)
                    : selectedReference;
                row.Bind(e, _selection.IsSelected(index), focused && index == _selection.Active, Highlighter, IsBookmarked?.Invoke(e) == true, reference, mode, display);
            }
            else
            {
                row.Index = -1;
                row.Visibility = Visibility.Collapsed;
            }
        }

        SyncScrollBar();

        var atEnd = NewestFirst ? _viewport.First == 0 : _viewport.First >= _viewport.MaxFirst;
        if (atEnd != IsAtEnd)
        {
            IsAtEnd = atEnd;
            AtEndChanged?.Invoke(this, atEnd);
        }
    }

    private void SyncScrollBar()
    {
        _syncingScrollBar = true;
        try
        {
            VerticalBar.Minimum = 0;
            VerticalBar.Maximum = _viewport.MaxFirst;
            VerticalBar.ViewportSize = _viewport.VisibleRows;
            VerticalBar.LargeChange = _viewport.PageSize;
            VerticalBar.Value = _viewport.First;
            VerticalBar.Visibility = _viewport.MaxFirst > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    private void OnScrollBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingScrollBar && _viewport.ScrollTo((int)Math.Round(e.NewValue)))
        {
            Render();
        }
    }

    // ------------------------------------------------------------------ input

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RowsHost).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // 120 units per notch; three rows per notch, like a text editor.
        var rows = (int)Math.Round(-delta / 120.0 * 3);
        if (rows == 0)
        {
            rows = delta > 0 ? -1 : 1;
        }

        ScrollRows(rows);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        if (_source is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(RowsHost);
        var index = _viewport.First + (int)(point.Position.Y / _metrics.RowHeight);
        if (index < 0 || index >= _source.Count)
        {
            return;
        }

        if (point.Properties.IsRightButtonPressed && _selection.IsSelected(index))
        {
            return; // keep a multi-row selection for the context menu
        }

        if (IsDown(VirtualKey.Shift))
        {
            _selection.ExtendTo(index);
        }
        else if (IsDown(VirtualKey.Control))
        {
            _selection.Toggle(index);
        }
        else
        {
            _selection.SelectSingle(index);
        }

        Render();
        RaiseSelectionChanged();

        // A right-click selects the row but stays unhandled, or the context menu would never open.
        e.Handled = !point.Properties.IsRightButtonPressed;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_source is null || _source.Count == 0)
        {
            return;
        }

        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var active = _selection.Active < 0 ? _viewport.First : _selection.Active;
        var last = _source.Count - 1;

        int? target = e.Key switch
        {
            VirtualKey.Up => active - 1,
            VirtualKey.Down => active + 1,
            VirtualKey.PageUp => active - _viewport.PageSize,
            VirtualKey.PageDown => active + _viewport.PageSize,
            VirtualKey.Home => 0,
            VirtualKey.End => last,
            _ => null,
        };

        if (target is { } t)
        {
            // With nothing selected yet, the first arrow press selects the top visible row rather than skipping it.
            t = _selection.Active < 0 && e.Key is VirtualKey.Down or VirtualKey.Up ? active : Math.Clamp(t, 0, last);
            if (shift)
            {
                _selection.ExtendTo(t);
            }
            else
            {
                _selection.SelectSingle(t);
            }

            _viewport.EnsureVisible(t);
            Render();
            RaiseSelectionChanged();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == VirtualKey.A)
        {
            _selection.SelectAll();
            Render();
            RaiseSelectionChanged();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.C && _selection.Count > 0)
        {
            CopyRequested?.Invoke(this, shift);
            e.Handled = true;
        }
    }

    private void OnFocusChanged(object sender, RoutedEventArgs e) => Render(); // shows / hides the focus rectangle

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
