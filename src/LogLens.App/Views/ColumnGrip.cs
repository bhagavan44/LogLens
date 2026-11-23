using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LogLens.App.Views;

/// <summary>
/// The draggable edge of a column header (FR-G1). It only reports what the user did — how far the edge was dragged, an arrow-key
/// nudge, a double-click to reset — and the owner decides the width. Focusable, so a column can be resized from the keyboard too.
/// </summary>
public sealed partial class ColumnGrip : UserControl
{
    /// <summary>Pixels per arrow-key press.</summary>
    private const double KeyboardStep = 8;

    private bool _dragging;
    private double _startX;

    public ColumnGrip()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    /// <summary>+1 when moving the grip right widens its column (the grip is on the column's right edge), -1 when it narrows it.</summary>
    public int Direction { get; set; } = 1;

    /// <summary>A drag began; remember the column's width.</summary>
    public event EventHandler? DragStarted;

    /// <summary>The pointer moved: how much wider (negative: narrower) the column should be than when the drag began.</summary>
    public event EventHandler<double>? DragDelta;

    /// <summary>The drag ended; the width can be saved.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>An arrow key was pressed: make the column this much wider (negative: narrower).</summary>
    public event EventHandler<double>? Nudged;

    /// <summary>Double-click, or Home: back to the automatic width.</summary>
    public event EventHandler? ResetRequested;

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }

        // Window coordinates: the grip itself moves while the column is resized.
        _startX = e.GetCurrentPoint(null).Position.X;
        _dragging = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            DragDelta?.Invoke(this, (e.GetCurrentPoint(null).Position.X - _startX) * Direction);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleasePointerCapture(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        ResetRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case VirtualKey.Left:
                Nudged?.Invoke(this, -KeyboardStep * Direction);
                break;
            case VirtualKey.Right:
                Nudged?.Invoke(this, KeyboardStep * Direction);
                break;
            case VirtualKey.Home:
                ResetRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
