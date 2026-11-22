namespace LogLens.Core.Collections;

/// <summary>
/// Scroll arithmetic for a row-virtualised grid: which row is at the top and how many fit. Pure maths, so it is unit-tested
/// and the WinUI control only has to translate input into these calls.
/// </summary>
public sealed class GridViewport
{
    public int ItemCount { get; private set; }

    /// <summary>Number of rows that fit completely in the viewport (at least 1).</summary>
    public int VisibleRows { get; private set; } = 1;

    /// <summary>Index of the row at the top of the viewport.</summary>
    public int First { get; private set; }

    public int MaxFirst => Math.Max(0, ItemCount - VisibleRows);

    /// <summary>How far PageUp/PageDown move: one row of overlap is kept so the reader doesn't lose their place.</summary>
    public int PageSize => Math.Max(1, VisibleRows - 1);

    /// <summary>Updates the list size and viewport height, keeping <see cref="First"/> in range.</summary>
    public void Configure(int itemCount, int visibleRows)
    {
        ItemCount = Math.Max(0, itemCount);
        VisibleRows = Math.Max(1, visibleRows);
        First = Math.Clamp(First, 0, MaxFirst);
    }

    /// <summary>Scrolls so that <paramref name="first"/> is at the top (clamped). Returns true if the position changed.</summary>
    public bool ScrollTo(int first)
    {
        var clamped = Math.Clamp(first, 0, MaxFirst);
        if (clamped == First)
        {
            return false;
        }

        First = clamped;
        return true;
    }

    public bool ScrollBy(int rows) => ScrollTo(First + rows);

    /// <summary>Scrolls the minimum amount needed to bring row <paramref name="index"/> fully into view.</summary>
    public bool EnsureVisible(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return false;
        }

        if (index < First)
        {
            return ScrollTo(index);
        }

        if (index >= First + VisibleRows)
        {
            return ScrollTo(index - VisibleRows + 1);
        }

        return false;
    }

    /// <summary>Scrolls so the row sits roughly in the middle (used when jumping to a search hit or bookmark).</summary>
    public bool Center(int index) => index >= 0 && index < ItemCount && ScrollTo(index - (VisibleRows / 2));
}
