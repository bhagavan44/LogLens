using LogLens.Core.Collections;

namespace LogLens.Core.Tests;

public class SelectionModelTests
{
    private static SelectionModel Model(int items)
    {
        var m = new SelectionModel();
        m.Reset(items);
        return m;
    }

    [Fact]
    public void Starts_empty_with_no_anchor_or_active_row()
    {
        var m = Model(10);

        Assert.Equal(0, m.Count);
        Assert.Equal(-1, m.Anchor);
        Assert.Equal(-1, m.Active);
        Assert.Empty(m.SelectedIndices());
    }

    [Fact]
    public void SelectSingle_replaces_any_previous_selection()
    {
        var m = Model(10);
        m.SelectSingle(2);
        m.SelectSingle(5);

        Assert.Equal([5], m.SelectedIndices());
        Assert.Equal(1, m.Count);
        Assert.Equal(5, m.Anchor);
        Assert.Equal(5, m.Active);
    }

    [Fact]
    public void Toggle_adds_and_removes_rows_and_moves_the_anchor()
    {
        var m = Model(10);
        m.SelectSingle(1);

        m.Toggle(4);
        m.Toggle(6);
        m.Toggle(4);

        Assert.Equal([1, 6], m.SelectedIndices());
        Assert.Equal(2, m.Count);
        Assert.Equal(4, m.Anchor);
    }

    [Fact]
    public void ExtendTo_selects_the_range_from_the_anchor_in_both_directions()
    {
        var m = Model(20);
        m.SelectSingle(10);

        m.ExtendTo(13);
        Assert.Equal([10, 11, 12, 13], m.SelectedIndices());
        Assert.Equal(13, m.Active);
        Assert.Equal(10, m.Anchor);

        m.ExtendTo(8);
        Assert.Equal([8, 9, 10], m.SelectedIndices());
        Assert.Equal(3, m.Count);
        Assert.Equal(10, m.Anchor);
    }

    [Fact]
    public void ExtendTo_without_an_anchor_starts_at_the_target()
    {
        var m = Model(10);

        m.ExtendTo(4);

        Assert.Equal([4], m.SelectedIndices());
        Assert.Equal(4, m.Anchor);
    }

    [Fact]
    public void SelectAll_and_Clear()
    {
        var m = Model(1_000_000);

        m.SelectAll();
        Assert.Equal(1_000_000, m.Count);
        Assert.True(m.IsSelected(999_999));

        m.Clear();
        Assert.Equal(0, m.Count);
        Assert.False(m.IsSelected(0));
    }

    [Fact]
    public void Out_of_range_indices_are_ignored()
    {
        var m = Model(5);

        m.SelectSingle(-1);
        m.SelectSingle(5);
        m.Toggle(99);
        m.ExtendTo(-3);

        Assert.Equal(0, m.Count);
        Assert.False(m.IsSelected(-1));
        Assert.False(m.IsSelected(5));
    }

    [Fact]
    public void Reset_resizes_and_clears()
    {
        var m = Model(10);
        m.SelectAll();

        m.Reset(3);

        Assert.Equal(3, m.ItemCount);
        Assert.Equal(0, m.Count);
        Assert.Equal(-1, m.Active);
    }

    [Fact]
    public void Growing_keeps_the_selection_and_the_active_row()
    {
        var m = Model(10);
        m.SelectSingle(3);
        m.ExtendTo(6);

        m.Resize(1000);

        Assert.Equal(1000, m.ItemCount);
        Assert.Equal([3, 4, 5, 6], m.SelectedIndices());
        Assert.Equal(4, m.Count);
        Assert.Equal(6, m.Active);
        Assert.Equal(3, m.Anchor);
    }

    [Fact]
    public void Shrinking_drops_rows_beyond_the_end_and_clamps_active_and_anchor()
    {
        var m = Model(10);
        m.SelectSingle(8);
        m.ExtendTo(9);

        m.Resize(9);

        Assert.Equal([8], m.SelectedIndices());
        Assert.Equal(1, m.Count);
        Assert.Equal(8, m.Active);
        Assert.Equal(8, m.Anchor);

        m.Resize(0);
        Assert.Equal(0, m.Count);
        Assert.Equal(-1, m.Active);
    }

    [Fact]
    public void Resizing_to_the_same_size_changes_nothing()
    {
        var m = Model(5);
        m.SelectAll();

        m.Resize(5);

        Assert.Equal(5, m.Count);
    }

    [Fact]
    public void Empty_model_is_safe()
    {
        var m = Model(0);

        m.SelectAll();
        m.SelectSingle(0);

        Assert.Equal(0, m.Count);
    }
}

public class GridViewportTests
{
    private static GridViewport Viewport(int items, int rows, int first = 0)
    {
        var v = new GridViewport();
        v.Configure(items, rows);
        v.ScrollTo(first);
        return v;
    }

    [Fact]
    public void Max_first_leaves_a_full_last_page()
    {
        var v = Viewport(100, 10);

        Assert.Equal(90, v.MaxFirst);
        Assert.Equal(9, v.PageSize);
    }

    [Fact]
    public void Short_lists_never_scroll()
    {
        var v = Viewport(5, 10);

        Assert.Equal(0, v.MaxFirst);
        Assert.False(v.ScrollBy(3));
        Assert.Equal(0, v.First);
    }

    [Theory]
    [InlineData(0, 3, 3)]
    [InlineData(50, -10, 40)]
    [InlineData(5, -100, 0)]
    [InlineData(85, 100, 90)]
    public void ScrollBy_clamps_to_the_valid_range(int start, int delta, int expected)
    {
        var v = Viewport(100, 10, start);

        v.ScrollBy(delta);

        Assert.Equal(expected, v.First);
    }

    [Fact]
    public void ScrollTo_reports_whether_anything_changed()
    {
        var v = Viewport(100, 10, 20);

        Assert.False(v.ScrollTo(20));
        Assert.True(v.ScrollTo(21));
    }

    [Theory]
    [InlineData(25, 20, 20)]  // already visible: no movement
    [InlineData(29, 20, 20)]  // last visible row
    [InlineData(30, 20, 21)]  // one below: scroll just enough
    [InlineData(5, 20, 5)]    // above: becomes the top row
    [InlineData(99, 0, 90)]   // far below: clamped so the last page is full
    public void EnsureVisible_scrolls_the_minimum(int index, int first, int expectedFirst)
    {
        var v = Viewport(100, 10, first);

        v.EnsureVisible(index);

        Assert.Equal(expectedFirst, v.First);
    }

    [Fact]
    public void EnsureVisible_ignores_invalid_indices()
    {
        var v = Viewport(100, 10, 20);

        Assert.False(v.EnsureVisible(-1));
        Assert.False(v.EnsureVisible(100));
        Assert.Equal(20, v.First);
    }

    [Fact]
    public void Center_puts_the_row_mid_viewport()
    {
        var v = Viewport(1000, 10);

        v.Center(500);

        Assert.Equal(495, v.First);
    }

    [Fact]
    public void Shrinking_the_list_pulls_the_viewport_back_in_range()
    {
        var v = Viewport(1000, 10, 900);

        v.Configure(50, 10);

        Assert.Equal(40, v.First);
    }

    [Fact]
    public void Handles_a_million_rows()
    {
        var v = Viewport(1_000_000, 40);

        v.ScrollTo(int.MaxValue);

        Assert.Equal(999_960, v.First);
    }
}
