using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace LogLens.App.Views;

/// <summary>
/// UI Automation for the <see cref="LogGrid"/> (UX-5): screen readers see a multi-select list whose items are the visible rows, each with
/// its position in the whole log ("3 of 250,000"), selection state and the row text. The grid only ever has visuals for the rows on
/// screen, so the list reports those as its children and exposes scrolling for the rest — the same model a virtualising list uses.
/// </summary>
internal sealed class LogGridAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider, IScrollProvider
{
    private readonly LogGrid _grid;

    public LogGridAutomationPeer(LogGrid grid)
        : base(grid)
    {
        _grid = grid;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(LogGrid);

    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Selection or PatternInterface.Scroll ? this : base.GetPatternCore(patternInterface);

    protected override IList<AutomationPeer> GetChildrenCore()
        => _grid.VisibleRows().Select(row => CreatePeerForElement(row)).Where(p => p is not null).ToList();

    /// <summary>The active row counts as the focused element, so Narrator reads the row (not just "Log events") as the selection moves.</summary>
    protected override object? GetFocusedElementCore()
        => _grid.ActiveRowVisual() is { } row ? CreatePeerForElement(row) : base.GetFocusedElementCore();

    // ----- ISelectionProvider

    public bool CanSelectMultiple => true;

    public bool IsSelectionRequired => false;

    public IRawElementProviderSimple[] GetSelection()
        => _grid.VisibleRows()
            .Where(r => _grid.IsRowSelected(r.Index))
            .Select(r => ProviderFromPeer(CreatePeerForElement(r)))
            .ToArray();

    // ----- IScrollProvider (vertical only)

    public bool HorizontallyScrollable => false;

    public double HorizontalScrollPercent => ScrollPatternIdentifiers.NoScroll;

    public double HorizontalViewSize => 100;

    public bool VerticallyScrollable => _grid.MaxFirstRow > 0;

    public double VerticalScrollPercent => _grid.MaxFirstRow > 0 ? 100.0 * _grid.FirstRow / _grid.MaxFirstRow : ScrollPatternIdentifiers.NoScroll;

    public double VerticalViewSize => _grid.RowCount == 0 ? 100 : Math.Min(100, 100.0 * _grid.VisibleRowCount / _grid.RowCount);

    public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        var rows = verticalAmount switch
        {
            ScrollAmount.LargeDecrement => -_grid.VisibleRowCount,
            ScrollAmount.SmallDecrement => -1,
            ScrollAmount.SmallIncrement => 1,
            ScrollAmount.LargeIncrement => _grid.VisibleRowCount,
            _ => 0,
        };
        _grid.ScrollRows(rows);
    }

    public void SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        if (verticalPercent >= 0)
        {
            _grid.ScrollToRow((int)Math.Round(_grid.MaxFirstRow * Math.Clamp(verticalPercent, 0, 100) / 100));
        }
    }
}

/// <summary>One visible row as a list item: selection, scroll-into-view and keyboard focus act on the grid's selection model.</summary>
internal sealed class LogRowAutomationPeer : FrameworkElementAutomationPeer, ISelectionItemProvider, IScrollItemProvider
{
    private readonly LogRow _row;

    public LogRowAutomationPeer(LogRow row)
        : base(row)
    {
        _row = row;
    }

    private LogGrid? Grid => _row.Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => nameof(LogRow);

    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.SelectionItem or PatternInterface.ScrollItem ? this : base.GetPatternCore(patternInterface);

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() => Grid is { } g && g.FocusState != FocusState.Unfocused && ReferenceEquals(g.ActiveRowVisual(), _row);

    protected override void SetFocusCore() => Grid?.SelectRowFromAutomation(_row.Index, SelectionChange.Single);

    protected override bool IsOffscreenCore() => _row.Visibility != Visibility.Visible;

    // ----- ISelectionItemProvider

    public bool IsSelected => Grid?.IsRowSelected(_row.Index) == true;

    public IRawElementProviderSimple? SelectionContainer
        => Grid is { } g && CreatePeerForElement(g) is { } peer ? ProviderFromPeer(peer) : null;

    public void Select() => Grid?.SelectRowFromAutomation(_row.Index, SelectionChange.Single);

    public void AddToSelection() => Grid?.SelectRowFromAutomation(_row.Index, SelectionChange.Add);

    public void RemoveFromSelection() => Grid?.SelectRowFromAutomation(_row.Index, SelectionChange.Remove);

    // ----- IScrollItemProvider: a row with a peer is already on screen

    public void ScrollIntoView()
    {
    }
}

internal enum SelectionChange
{
    Single,
    Add,
    Remove,
}
