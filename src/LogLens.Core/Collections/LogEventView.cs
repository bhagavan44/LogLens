using System.Collections;
using LogLens.Core.Models;

namespace LogLens.Core.Collections;

/// <summary>
/// What the grid shows: every event of a document, or the subset left by a filter. Applying or clearing a filter never copies events, only
/// a sorted array of event indices is swapped in.
/// </summary>
/// <remarks>Read-only for its users; <see cref="SetFilter"/> is called on the UI thread once a filter pass has finished.</remarks>
public sealed class LogEventView : IReadOnlyList<LogEvent>
{
    private readonly IReadOnlyList<LogEvent> _source;
    private int[]? _indices;

    public LogEventView(IReadOnlyList<LogEvent> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Number of events ignoring any filter.</summary>
    public int TotalCount => _source.Count;

    public bool IsFiltered => _indices is not null;

    public int Count => _indices?.Length ?? _source.Count;

    public LogEvent this[int index]
    {
        get
        {
            if (_indices is null)
            {
                return _source[index];
            }

            if ((uint)index >= (uint)_indices.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _source[_indices[index]];
        }
    }

    /// <summary>Restricts the view to the given ascending indices into the source, or shows every event when null.</summary>
    public void SetFilter(int[]? matchingIndices) => _indices = matchingIndices;

    /// <summary>The row showing <paramref name="item"/>, or -1 when it is not in the view (hidden by the filter, or from another document).</summary>
    public int IndexOf(LogEvent? item)
    {
        if (item is null || (uint)item.Id >= (uint)_source.Count || !ReferenceEquals(_source[item.Id], item))
        {
            return -1;
        }

        if (_indices is null)
        {
            return item.Id;
        }

        var pos = Array.BinarySearch(_indices, item.Id);
        return pos >= 0 ? pos : -1;
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
        var count = indices?.Length ?? _source.Count;
        var result = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (predicate(_source[indices is null ? i : indices[i]]))
            {
                result.Add(i);
            }
        }

        return [.. result];
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
