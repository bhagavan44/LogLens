using System.Collections;
using LogLens.Core.Models;

namespace LogLens.Core.Collections;

/// <summary>
/// What the grid shows: every event of a document, or the subset left by a filter. Applying or clearing a filter never copies events, only
/// a sorted array of event indices is swapped in. When the document grows (progressive load, live tail) <see cref="Sync"/> brings the view in
/// line by testing only the new events.
/// </summary>
/// <remarks>
/// Read-only for its users; <see cref="SetFilter"/> and <see cref="Sync"/> are called on the UI thread. With <see cref="NewestFirst"/> (FR-G3)
/// the rows are the same events in reverse: row 0 is the last event. Nothing is copied, only the row ↔ position mapping changes, so new events
/// then appear at the <em>top</em>.
/// </remarks>
public sealed class LogEventView : IReadOnlyList<LogEvent>
{
    private readonly IReadOnlyList<LogEvent> _source;
    private int[]? _indices;
    private int _indexCount;
    private int _sourceSeen;

    public LogEventView(IReadOnlyList<LogEvent> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceSeen = source.Count;
    }

    /// <summary>Number of events ignoring any filter.</summary>
    public int TotalCount => _source.Count;

    public bool IsFiltered => _indices is not null;

    public int Count => _indices is null ? _source.Count : _indexCount;

    /// <summary>Rows run from the newest event down to the oldest (FR-G3). Change it with <see cref="SetNewestFirst"/>.</summary>
    public bool NewestFirst { get; private set; }

    /// <summary>The event in row <paramref name="index"/> (row 0 is the oldest listed event, or the newest with <see cref="NewestFirst"/>).</summary>
    public LogEvent this[int index] => At(NewestFirst ? Count - 1 - index : index);

    /// <summary>The listed events oldest first whatever the row order, for work that needs file order (export, statistics, go to a time).</summary>
    public IReadOnlyList<LogEvent> Chronological => NewestFirst ? new OldestFirst(this) : this;

    /// <summary>The row showing the event at <paramref name="position"/> of <see cref="Chronological"/>.</summary>
    public int RowOfPosition(int position) => NewestFirst && position >= 0 ? Count - 1 - position : position;

    /// <summary>Turns the row order round. Every row moved, so the caller refreshes the grid. Returns false when the order already was that.</summary>
    public bool SetNewestFirst(bool newestFirst)
    {
        if (NewestFirst == newestFirst)
        {
            return false;
        }

        NewestFirst = newestFirst;
        return true;
    }

    // The event at a position counted from the oldest listed event.
    private LogEvent At(int position)
    {
        if (_indices is null)
        {
            return _source[position];
        }

        if ((uint)position >= (uint)_indexCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return _source[_indices[position]];
    }

    /// <summary>Restricts the view to the given ascending indices into the source, or shows every event when null.</summary>
    public void SetFilter(int[]? matchingIndices)
    {
        _indices = matchingIndices;
        _indexCount = matchingIndices?.Length ?? 0;
        _sourceSeen = _source.Count;
    }

    /// <summary>
    /// The source changed from index <paramref name="firstChanged"/> on (events there were removed and / or appended). Drops the rows of removed
    /// events and tests the new ones against <paramref name="predicate"/> (null = no filter), so the rows before the change stay as they are.
    /// </summary>
    /// <returns>
    /// The first row that changed and how many rows were removed and added there. With <see cref="NewestFirst"/> the change is at the top: the
    /// first row is 0, rows 0 … removed-1 went, rows 0 … added-1 are new and every other row moved down by <c>added - removed</c>.
    /// </returns>
    public (int FirstRow, int RowsRemoved, int RowsAdded) Sync(Func<LogEvent, bool>? predicate, int firstChanged)
    {
        var count = _source.Count;
        var seen = _sourceSeen;
        firstChanged = Math.Clamp(firstChanged, 0, Math.Min(seen, count));
        _sourceSeen = count;

        if (_indices is null || predicate is null)
        {
            if (_indices is not null)
            {
                // Filtered, but nothing filters any more: every row may have changed.
                var before = _indexCount;
                _indices = null;
                _indexCount = 0;
                return (0, before, count);
            }

            return (NewestFirst ? 0 : firstChanged, Math.Max(0, seen - firstChanged), count - firstChanged);
        }

        var keep = _indexCount;
        while (keep > 0 && _indices[keep - 1] >= firstChanged)
        {
            keep--;
        }

        var removed = _indexCount - keep;
        _indexCount = keep;
        for (var i = firstChanged; i < count; i++)
        {
            if (predicate(_source[i]))
            {
                if (_indexCount == _indices.Length)
                {
                    Array.Resize(ref _indices, Math.Max(16, _indices.Length * 2));
                }

                _indices[_indexCount++] = i;
            }
        }

        return (NewestFirst ? 0 : keep, removed, _indexCount - keep);
    }

    /// <summary>The row showing <paramref name="item"/>, or -1 when it is not in the view (hidden by the filter, or from another document).</summary>
    public int IndexOf(LogEvent? item)
    {
        if (item is null || (uint)item.Id >= (uint)_source.Count || !ReferenceEquals(_source[item.Id], item))
        {
            return -1;
        }

        if (_indices is null)
        {
            return RowOfPosition(item.Id);
        }

        var pos = Array.BinarySearch(_indices, 0, _indexCount, item.Id);
        return pos >= 0 ? RowOfPosition(pos) : -1;
    }

    /// <summary>Indices of the source events matching <paramref name="predicate"/>. Safe off the UI thread while the source does not change.</summary>
    public static int[] ComputeIndices(IReadOnlyList<LogEvent> source, Func<LogEvent, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);
        var count = source.Count;
        var result = new List<int>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (predicate(source[i]))
            {
                result.Add(i);
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Rows of this view whose event matches <paramref name="predicate"/>. Safe off the UI thread while the source does not change; it works on
    /// the filter in place when it started, so the caller discards the result if <see cref="SetFilter"/> was called meanwhile.
    /// </summary>
    public int[] FindRows(Func<LogEvent, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var indices = _indices;
        var count = indices is null ? _source.Count : _indexCount;
        var newestFirst = NewestFirst;
        var result = new List<int>();
        for (var row = 0; row < count; row++)
        {
            if ((row & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var position = newestFirst ? count - 1 - row : row;
            if (predicate(_source[indices is null ? position : indices[position]]))
            {
                result.Add(row);
            }
        }

        return [.. result];
    }

    /// <summary>The same events oldest first; a thin wrapper, nothing is copied.</summary>
    private sealed class OldestFirst(LogEventView view) : IReadOnlyList<LogEvent>
    {
        public int Count => view.Count;

        public LogEvent this[int index] => view.At(index);

        public IEnumerator<LogEvent> GetEnumerator()
        {
            var count = Count;
            for (var i = 0; i < count; i++)
            {
                yield return view.At(i);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public IEnumerator<LogEvent> GetEnumerator()
    {
        var count = Count;
        for (var i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
