using System.Text;

namespace LogLens.Core.Models;

/// <summary>
/// The decoded text of a log, held as a list of chunks so a growing file (progressive load, tail) never copies everything on each append.
/// Events refer to it by absolute character offset; <see cref="Substring"/> stitches together a range that crosses a chunk boundary.
/// </summary>
/// <remarks>
/// <para>
/// A chunk that is pure ASCII (most logs) is kept as one byte per character instead of a .NET string's two (NFR-5): offsets stay the same, because
/// each character is still one unit, and the text comes back unchanged. Any other chunk is kept as a string.
/// </para>
/// <para>Not thread-safe: built on one thread, then only read.</para>
/// </remarks>
public sealed class TextStore
{
    // Small appends (typical while tailing) are merged into the previous chunk so the chunk list stays short.
    private const int MergeLimit = 256 * 1024;

    // Each chunk is a string, or a byte[] holding ASCII text one byte per character.
    private readonly List<object> _chunks = [];
    private readonly List<long> _starts = [];
    private readonly List<long> _lineStarts = [0];
    private long _indexedUpTo;

    /// <summary>Total number of characters.</summary>
    public long Length { get; private set; }

    public int ChunkCount => _chunks.Count;

    /// <summary>Bytes the text takes up in memory (without the line index): one per character of an ASCII chunk, two otherwise.</summary>
    public long StoredBytes => _chunks.Sum(c => c is byte[] bytes ? bytes.LongLength : 2L * ((string)c).Length);

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        if (_chunks.Count > 0 && LengthOf(_chunks[^1]) + text.Length <= MergeLimit)
        {
            _chunks[^1] = _chunks[^1] switch
            {
                byte[] ascii when Ascii.IsValid(text) => Concat(ascii, text),
                byte[] ascii => string.Concat(Encoding.ASCII.GetString(ascii), text),
                var chunk => string.Concat((string)chunk, text),
            };
        }
        else
        {
            _chunks.Add(Ascii.IsValid(text) ? Encoding.ASCII.GetBytes(text) : text);
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
        if (offsetInChunk + length <= LengthOf(chunk))
        {
            return chunk is byte[] ascii ? Encoding.ASCII.GetString(ascii, offsetInChunk, length) : ((string)chunk).Substring(offsetInChunk, length);
        }

        return string.Create(length, (Store: this, Index: index, Offset: offsetInChunk), static (destination, state) =>
        {
            var (store, index, offset) = state;
            while (destination.Length > 0)
            {
                var chunk = store._chunks[index++];
                var take = Math.Min(destination.Length, LengthOf(chunk) - offset);
                CopyTo(chunk, offset, take, destination);
                destination = destination[take..];
                offset = 0;
            }
        });
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
            var from = (int)Math.Max(0, _indexedUpTo - _starts[c]);
            var pos = NextNewline(_chunks[c], from);
            while (pos >= 0)
            {
                _lineStarts.Add(_starts[c] + pos + 1);
                pos = NextNewline(_chunks[c], pos + 1);
            }
        }

        _indexedUpTo = Length;
    }

    private int ChunkAt(long start)
    {
        var index = _starts.BinarySearch(start);
        return index >= 0 ? index : ~index - 1; // the chunk that starts at or before 'start'
    }

    private static int LengthOf(object chunk) => chunk is byte[] ascii ? ascii.Length : ((string)chunk).Length;

    private static int NextNewline(object chunk, int from)
    {
        if (chunk is byte[] ascii)
        {
            var at = ascii.AsSpan(from).IndexOf((byte)'\n');
            return at < 0 ? -1 : from + at;
        }

        return ((string)chunk).IndexOf('\n', from);
    }

    private static void CopyTo(object chunk, int offset, int length, Span<char> destination)
    {
        if (chunk is byte[] ascii)
        {
            Ascii.ToUtf16(ascii.AsSpan(offset, length), destination, out _);
        }
        else
        {
            ((string)chunk).AsSpan(offset, length).CopyTo(destination);
        }
    }

    private static byte[] Concat(byte[] ascii, string text)
    {
        var result = new byte[ascii.Length + text.Length];
        ascii.CopyTo(result, 0);
        Ascii.FromUtf16(text, result.AsSpan(ascii.Length), out _);
        return result;
    }
}
