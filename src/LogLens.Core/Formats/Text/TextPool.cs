namespace LogLens.Core.Formats.Text;

/// <summary>
/// Hands back the same string for text it saw recently, so a stack trace logged again and again is held in memory once instead of once per
/// event (NFR-5). A small direct-mapped table: a hit costs a hash and a compare and allocates nothing; a miss allocates the string as usual.
/// </summary>
/// <remarks>Not thread-safe: one per parser.</remarks>
internal sealed class TextPool
{
    private const int Size = 256; // a power of two

    private readonly string?[] _slots = new string?[Size];

    /// <summary>A string equal to <paramref name="text"/>: one seen recently when there is one, otherwise a new one (kept for next time).</summary>
    public string Get(ReadOnlySpan<char> text)
    {
        var slot = string.GetHashCode(text) & (Size - 1);
        if (_slots[slot] is { } known && text.SequenceEqual(known))
        {
            return known;
        }

        var created = text.ToString();
        _slots[slot] = created;
        return created;
    }
}
