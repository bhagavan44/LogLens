namespace LogLens.Core.Models;

/// <summary>
/// The decoded text of a log, held as a list of chunks so a growing file (progressive load, tail) never copies everything on each append.
/// Events refer to it by absolute character offset; <see cref="Substring"/> stitches together a range that crosses a chunk boundary.
/// </summary>
/// <remarks>Not thread-safe: built on one thread, then only read.</remarks>
public sealed class TextStore
{
    // Small appends (typical while tailing) are merged into the previous chunk so the chunk list stays short.
    private const int MergeLimit = 256 * 1024;

    private readonly List<string> _chunks = [];
    private readonly List<long> _starts = [];
    private readonly List<long> _lineStarts = [0];
    private long _indexedUpTo;

    /// <summary>Total number of characters.</summary>
    public long Length { get; private set; }

    public int ChunkCount => _chunks.Count;

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        if (_chunks.Count > 0 && _chunks[^1].Length + text.Length <= MergeLimit)
        {
            _chunks[^1] = string.Concat(_chunks[^1], text);
        }
        else
        {
            _chunks.Add(text);
            _starts.Add(Length);
        }

        Length += text.Length;
    }

    public void Clear()
    {
        _chunks.Clear();
        _starts.Clear();
        Length = 0;
        _lineStarts.Clear();
        _lineStarts.Add(0);
        _indexedUpTo = 0;
    }

    /// <summary>The characters in <c>[start, start + length)</c>.</summary>
    public string Substring(long start, int length)
    {
        if (start < 0 || length < 0 || start + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"Range {start}+{length} is outside the text (length {Length}).");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var index = ChunkAt(start);
        var offsetInChunk = (int)(start - _starts[index]);
        var chunk = _chunks[index];
        if (offsetInChunk + length <= chunk.Length)
        {
            return chunk.Substring(offsetInChunk, length);
        }

        var result = new System.Text.StringBuilder(length);
        var remaining = length;
        while (remaining > 0)
        {
            chunk = _chunks[index];
            var take = Math.Min(remaining, chunk.Length - offsetInChunk);
            result.Append(chunk, offsetInChunk, take);
            remaining -= take;
            offsetInChunk = 0;
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// The 1-based line number of the character at <paramref name="offset"/> (FR-D4). Uses an index of line starts (8 bytes per line) that is
    /// extended over newly appended text when needed, so a growing log is never re-scanned from its start; then a binary search.
    /// </summary>
    public int LineNumberAt(long offset)
    {
        if (offset < 0 || offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        ExtendLineIndex();
        var index = _lineStarts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    private void ExtendLineIndex()
    {
        if (_indexedUpTo == Length)
        {
            return;
        }

        for (var c = ChunkAt(_indexedUpTo); c < _chunks.Count; c++)
        {
            var chunk = _chunks[c];
            var pos = chunk.IndexOf('\n', (int)Math.Max(0, _indexedUpTo - _starts[c]));
            while (pos >= 0)
            {
                _lineStarts.Add(_starts[c] + pos + 1);
                pos = chunk.IndexOf('\n', pos + 1);
            }
        }

        _indexedUpTo = Length;
    }

    private int ChunkAt(long start)
    {
        var index = _starts.BinarySearch(start);
        return index >= 0 ? index : ~index - 1; // the chunk that starts at or before 'start'
    }
}
