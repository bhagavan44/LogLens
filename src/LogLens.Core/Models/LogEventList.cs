using System.Collections;

namespace LogLens.Core.Models;

/// <summary>
/// A growable list of events whose last entries can be replaced (the event that is still being written while a file is read in chunks).
/// </summary>
public sealed class LogEventList : IReadOnlyList<LogEvent>
{
    private LogEvent[] _items;
    private int _count;

    public LogEventList(int capacity = 0)
    {
        _items = capacity > 0 ? new LogEvent[capacity] : [];
    }

    public int Count => _count;

    public LogEvent this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _items[index];
        }
    }

    public void Add(LogEvent item)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, Math.Max(16, _count * 2));
        }

        _items[_count++] = item;
    }

    /// <summary>Removes every event at <paramref name="count"/> and beyond.</summary>
    public void TruncateTo(int count)
    {
        if (count < 0 || count > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Array.Clear(_items, count, _count - count);
        _count = count;
    }

    public void Clear() => TruncateTo(0);

    public IEnumerator<LogEvent> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _items[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
