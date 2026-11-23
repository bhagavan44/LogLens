using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using LogLens.Core.Analysis;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.App.Views;

/// <summary>
/// The timeline histogram (FR-N2): events over time as bars stacked by level. Dragging across it (or clicking one bar) raises
/// <see cref="RangeSelected"/> so the list can be narrowed to that time range; the range currently applied is drawn as a band.
/// </summary>
public sealed partial class TimelineStrip : UserControl
{
    private const double DragThreshold = 4;

    // Bottom to top: quiet levels first so errors sit on top and stay visible on a busy bar.
    private static readonly LogLevel[] StackOrder =
    [
        LogLevel.Verbose, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Fatal, LogLevel.Unknown,
    ];

    private TimelineData _data = TimelineData.Empty;
    private TimeRange _window = new(null, null);
    private bool _dragging;
    private double _dragStartX;

    public TimelineStrip()
    {
        InitializeComponent();
        Redraw();
    }

    /// <summary>Raised when the user picks a time range; the times are wall-clock readings in the display zone.</summary>
    public event EventHandler<(DateTime From, DateTime To)>? RangeSelected;

    /// <summary>Raised by "Clear time range".</summary>
    public event EventHandler? ClearRequested;

    public TimelineData Data
    {
        get => _data;
        set
        {
            _data = value ?? TimelineData.Empty;
            Redraw();
        }
    }

    /// <summary>The time window currently applied to the list, drawn as a highlighted band.</summary>
    public TimeRange AppliedWindow
    {
        get => _window;
        set
        {
            _window = value ?? TimeRange.Open;
            Redraw(); // the bars outside the window are dimmed, so they are drawn again too
        }
    }

    private double SurfaceWidth => Math.Max(0, Surface.ActualWidth);

    private double SurfaceHeight => Math.Max(0, Surface.ActualHeight);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ------------------------------------------------------------------ drawing

    private void Redraw()
    {
        BarsCanvas.Children.Clear();
        EmptyText.Visibility = _data.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        StartText.Text = _data.IsEmpty ? string.Empty : Format(_data.Start);
        EndText.Text = _data.IsEmpty ? string.Empty : Format(_data.End);

        var width = SurfaceWidth;
        var height = SurfaceHeight;
        if (!_data.IsEmpty && width > 0 && height > 0)
        {
            DrawBars(width, height);
        }

        DrawWindow();
    }

    private void DrawBars(double width, double height)
    {
        var n = _data.BucketCount;
        var step = width / n;
        var barWidth = Math.Max(1, step >= 3 ? step - 1 : step);
        var scale = (height - 3) / Math.Max(1, _data.MaxTotal);

        // Bars outside the applied window are drawn dimmed, so the range the list is showing stands out.
        var dimOutside = !_window.IsOpen;
        var inside = StackOrder.ToDictionary(level => level, _ => new GeometryGroup());
        var outside = StackOrder.ToDictionary(level => level, _ => new GeometryGroup());
        for (var bucket = 0; bucket < n; bucket++)
        {
            var total = _data.Totals[bucket];
            if (total == 0)
            {
                continue;
            }

            var groups = dimOutside && !Overlaps(bucket) ? outside : inside;

            // A bucket with any events is at least 3 px tall so a lone error is not invisible next to a big spike.
            var barHeight = Math.Max(3, total * scale);
            var x = bucket * step;
            var y = height - 1;
            foreach (var level in StackOrder)
            {
                var count = _data.CountsByLevel[(int)level][bucket];
                if (count == 0)
                {
                    continue;
                }

                var segment = barHeight * count / total;
                y -= segment;
                groups[level].Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(x, y, barWidth, segment) });
            }
        }

        foreach (var (groups, opacity) in new[] { (outside, 0.35), (inside, 1.0) })
        {
            foreach (var level in StackOrder)
            {
                if (groups[level].Children.Count > 0)
                {
                    BarsCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = groups[level], Fill = LogFormat.BadgeBackground(level), Opacity = opacity });
                }
            }
        }
    }

    /// <summary>Whether a bucket overlaps the applied time window.</summary>
    private bool Overlaps(int bucket)
    {
        var start = _data.BucketStart(bucket);
        var end = _data.BucketStart(bucket + 1);
        return (_window.From is null || end > _window.From) && (_window.To is null || start <= _window.To);
    }

    private void DrawWindow()
    {
        var width = SurfaceWidth;
        var height = SurfaceHeight;
        var visible = !_data.IsEmpty && !_window.IsOpen && width > 0;
        WindowBand.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = !_window.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = _window.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        var left = XOf(_window.From ?? _data.Start);
        var right = XOf(_window.To ?? _data.End);
        if (right < left)
        {
            (left, right) = (right, left);
        }

        right = Math.Max(right, left + 4); // a very narrow window stays visible
        WindowBand.Width = right - left;
        WindowBand.Height = height;
        Canvas.SetLeft(WindowBand, left);
    }

    private double XOf(DateTime time)
    {
        var span = (_data.End - _data.Start).Ticks;
        return span <= 0 ? 0 : Math.Clamp((double)(time - _data.Start).Ticks / span, 0, 1) * SurfaceWidth;
    }

    private double FractionOf(double x) => SurfaceWidth <= 0 ? 0 : Math.Clamp(x / SurfaceWidth, 0, 1);

    // ------------------------------------------------------------------ pointer

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_data.IsEmpty || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragStartX = e.GetCurrentPoint(Surface).Position.X;
        Surface.CapturePointer(e.Pointer);
        UpdateDragBand(_dragStartX);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(Surface).Position.X;
        if (_dragging)
        {
            UpdateDragBand(x);
        }

        ToolTipService.SetToolTip(Surface, _data.IsEmpty ? null : Describe(_data.BucketAt(_data.TimeAt(FractionOf(x)))));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var x = e.GetCurrentPoint(Surface).Position.X;
        EndDrag(e.Pointer);

        if (_data.IsEmpty)
        {
            return;
        }

        if (Math.Abs(x - _dragStartX) < DragThreshold)
        {
            // A click selects the bar under the pointer.
            var bucket = _data.BucketAt(_data.TimeAt(FractionOf(x)));
            RangeSelected?.Invoke(this, (_data.BucketStart(bucket), _data.BucketStart(bucket + 1) - TimeSpan.FromMilliseconds(1)));
        }
        else
        {
            var from = _data.TimeAt(FractionOf(Math.Min(x, _dragStartX)));
            var to = _data.TimeAt(FractionOf(Math.Max(x, _dragStartX)));
            RangeSelected?.Invoke(this, (from, to));
        }

        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag(null);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => ToolTipService.SetToolTip(Surface, null);

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        _dragging = false;
        DragBand.Visibility = Visibility.Collapsed;
        if (pointer is not null)
        {
            Surface.ReleasePointerCapture(pointer);
        }
    }

    private void UpdateDragBand(double x)
    {
        x = Math.Clamp(x, 0, SurfaceWidth);
        var left = Math.Min(x, _dragStartX);
        DragBand.Visibility = Visibility.Visible;
        DragBand.Width = Math.Abs(x - _dragStartX);
        DragBand.Height = SurfaceHeight;
        Canvas.SetLeft(DragBand, left);
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ keyboard (UX-1, UX-5)

    private int _cursor = -1;
    private int _anchor = -1;

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_data.IsEmpty)
        {
            return;
        }

        // Start where the applied window starts, else at the first bar with events.
        _cursor = _window.From is { } from ? _data.BucketAt(from) : Array.FindIndex(_data.Totals, t => t > 0);
        _cursor = Math.Clamp(_cursor, 0, _data.BucketCount - 1);
        _anchor = _cursor;
        ShowKeyboardSelection();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _cursor = _anchor = -1;
        if (!_dragging)
        {
            DragBand.Visibility = Visibility.Collapsed;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_data.IsEmpty)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var last = _data.BucketCount - 1;
        int? target = e.Key switch
        {
            Windows.System.VirtualKey.Left => _cursor - 1,
            Windows.System.VirtualKey.Right => _cursor + 1,
            Windows.System.VirtualKey.PageUp => _cursor - 10,
            Windows.System.VirtualKey.PageDown => _cursor + 10,
            Windows.System.VirtualKey.Home => 0,
            Windows.System.VirtualKey.End => last,
            _ => null,
        };

        if (target is { } t)
        {
            _cursor = Math.Clamp(t, 0, last);
            if (!shift)
            {
                _anchor = _cursor;
            }

            ShowKeyboardSelection();
            e.Handled = true;
        }
        else if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && _cursor >= 0)
        {
            var (first, end) = (Math.Min(_anchor, _cursor), Math.Max(_anchor, _cursor));
            RangeSelected?.Invoke(this, (_data.BucketStart(first), _data.BucketStart(end + 1) - TimeSpan.FromMilliseconds(1)));
            Announce("Time range applied");
            e.Handled = true;
        }
        else if (e.Key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Delete && !_window.IsOpen)
        {
            ClearRequested?.Invoke(this, EventArgs.Empty);
            Announce("Time range cleared");
            e.Handled = true;
        }
    }

    /// <summary>Outlines the bars chosen with the keyboard and tells a screen reader what they hold.</summary>
    private void ShowKeyboardSelection()
    {
        if (_cursor < 0)
        {
            return;
        }

        var (first, end) = (Math.Min(_anchor, _cursor), Math.Max(_anchor, _cursor));
        var left = XOf(_data.BucketStart(first));
        var right = Math.Max(left + 3, XOf(_data.BucketStart(end + 1)));
        DragBand.Visibility = Visibility.Visible;
        DragBand.Width = right - left;
        DragBand.Height = SurfaceHeight;
        Canvas.SetLeft(DragBand, left);

        var text = first == end ? Describe(_cursor) : DescribeRange(first, end);
        Announce(text.Replace('\n', ' ') + (first == end ? ". Press Enter to show only this time." : ". Press Enter to show only this range."));
    }

    private string DescribeRange(int first, int last)
    {
        var total = 0;
        for (var i = first; i <= last; i++)
        {
            total += _data.Totals[i];
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Format(_data.BucketStart(first))} – {Format(_data.BucketStart(last + 1))}\n{total:N0} events in {last - first + 1} bars");
    }

    private void Announce(string text)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(this, text);
        if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(this) is { } peer)
        {
            peer.RaiseNotificationEvent(
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent,
                text,
                "TimelineSelection");
        }
    }

    // ------------------------------------------------------------------ text

    private string Describe(int bucket)
    {
        if (bucket < 0)
        {
            return string.Empty;
        }

        var from = _data.BucketStart(bucket);
        var to = _data.BucketStart(bucket + 1);
        var parts = new List<string>();
        foreach (var level in StackOrder.Reverse())
        {
            var count = _data.CountsByLevel[(int)level][bucket];
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count:N0} {LogText.LevelAbbreviation(level)}"));
            }
        }

        var total = _data.Totals[bucket];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Format(from)} – {Format(to)}\n{total:N0} event{(total == 1 ? string.Empty : "s")}{(parts.Count > 0 ? "  (" + string.Join(", ", parts) + ")" : string.Empty)}");
    }

    private static string Format(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
