using System.Collections;

namespace LogLens.Core.Collections;

/// <summary>
/// Row selection for a virtualised list: a bit per row (1 M rows ≈ 125 KB), plus the <em>anchor</em> (where a shift-selection starts)
/// and the <em>active</em> row (the one with keyboard focus, which the detail pane follows).
/// </summary>
public sealed class SelectionModel
{
    private BitArray _bits = new(0);

    public int ItemCount => _bits.Length;

    public int Count { get; private set; }

    public int Anchor { get; private set; } = -1;

    public int Active { get; private set; } = -1;

    /// <summary>Clears the selection and resizes it for a list of <paramref name="itemCount"/> rows.</summary>
    public void Reset(int itemCount)
    {
        _bits = new BitArray(Math.Max(0, itemCount));
        Count = 0;
        Anchor = Active = -1;
    }

    /// <summary>
    /// Changes the number of rows <b>keeping</b> what is selected (rows were added at the end, or the last ones replaced). Rows beyond the
    /// new size are dropped from the selection; the anchor and active row are clamped.
    /// </summary>
    public void Resize(int itemCount)
    {
        itemCount = Math.Max(0, itemCount);
        if (itemCount == _bits.Length)
        {
            return;
        }

        var resized = new BitArray(itemCount);
        var keep = Math.Min(itemCount, _bits.Length);
        var selected = 0;
        for (var i = 0; i < keep; i++)
        {
            if (_bits[i])
            {
                resized[i] = true;
                selected++;
            }
        }

        _bits = resized;
        Count = selected;
        if (Anchor >= itemCount)
        {
            Anchor = itemCount - 1;
        }

        if (Active >= itemCount)
        {
            Active = itemCount - 1;
        }
    }

    /// <summary>
    /// <paramref name="count"/> rows were inserted above the first one (new events in a newest-first list): everything selected, the anchor and
    /// the active row move down with their events. The new rows are not selected.
    /// </summary>
    public void InsertAtStart(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var shifted = new BitArray(_bits.Length + count);
        for (var i = 0; i < _bits.Length; i++)
        {
            if (_bits[i])
            {
                shifted[i + count] = true;
            }
        }

        _bits = shifted;
        if (Anchor >= 0)
        {
            Anchor += count;
        }

        if (Active >= 0)
        {
            Active += count;
        }
    }

    public bool IsSelected(int index) => (uint)index < (uint)_bits.Length && _bits[index];

    public void Clear()
    {
        _bits.SetAll(false);
        Count = 0;
    }

    /// <summary>Plain click or arrow key: only this row is selected.</summary>
    public void SelectSingle(int index)
    {
        if (!InRange(index))
        {
            return;
        }

        Clear();
        Set(index, true);
        Anchor = Active = index;
    }

    /// <summary>Ctrl+click: flips one row and makes it the new anchor.</summary>
    public void Toggle(int index)
    {
        if (!InRange(index))
        {
            return;
        }

        Set(index, !_bits[index]);
        Anchor = Active = index;
    }

    /// <summary>Shift+click / shift+arrow: selects everything between the anchor and <paramref name="index"/>.</summary>
    public void ExtendTo(int index)
    {
        if (!InRange(index))
        {
            return;
        }

        if (Anchor < 0)
        {
            Anchor = index;
        }

        Clear();
        var from = Math.Min(Anchor, index);
        var to = Math.Max(Anchor, index);
        for (var i = from; i <= to; i++)
        {
            _bits[i] = true;
        }

        Count = to - from + 1;
        Active = index;
    }

    public void SelectAll()
    {
        if (_bits.Length == 0)
        {
            return;
        }

        _bits.SetAll(true);
        Count = _bits.Length;
        if (Active < 0)
        {
            Active = Anchor = 0;
        }
    }

    /// <summary>Selected row indices in ascending order.</summary>
    public IEnumerable<int> SelectedIndices()
    {
        for (var i = 0; i < _bits.Length; i++)
        {
            if (_bits[i])
            {
                yield return i;
            }
        }
    }

    private bool InRange(int index) => (uint)index < (uint)_bits.Length;

    private void Set(int index, bool value)
    {
        if (_bits[index] == value)
        {
            return;
        }

        _bits[index] = value;
        Count += value ? 1 : -1;
    }
}
