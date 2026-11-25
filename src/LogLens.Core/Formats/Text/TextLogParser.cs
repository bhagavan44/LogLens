namespace LogLens.Core.Formats.Text;

/// <summary>
/// The incremental parser of every plain-text format: drives <see cref="PlainTextLogParser"/> chunk by chunk. The text of the event that is
/// still open is carried over and re-read with the next chunk, so no event is ever split between chunks.
/// </summary>
public sealed class TextLogParser : ILogParser
{
    private readonly PlainTextLogParser _parser;
    private readonly string? _sourceFile;
    private string _carry = string.Empty;
    private long _carryOffset;

    /// <param name="pattern">The event lines to recognise; Serilog's default file template when null.</param>
    public TextLogParser(LinePattern? pattern = null, string? sourceFile = null, long startOffset = 0, int firstId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(firstId);
        _parser = new PlainTextLogParser(pattern);
        _sourceFile = sourceFile;
        _carryOffset = startOffset;
        CommittedId = firstId;
    }

    public int CommittedId { get; private set; }

    public long EndOffset => _carryOffset + _carry.Length;

    public bool HasPartialLine => _carry.Length > 0 && _carry[^1] != '\n';

    public ParseDelta Append(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var work = _carry.Length == 0 ? text : string.Concat(_carry, text);
        var first = CommittedId;
        var result = _parser.ParseChunk(work, _carryOffset, first, _sourceFile, final: false, progress: null, cancellationToken);

        _carryOffset += result.CarryFrom;
        _carry = result.CarryFrom >= work.Length ? string.Empty : work[result.CarryFrom..];
        CommittedId = result.EndsOpen ? result.Events[^1].Id : first + result.Events.Count;
        return new ParseDelta(first, result.Events);
    }

    public ParseDelta Preview()
    {
        var result = _parser.ParseChunk(_carry, _carryOffset, CommittedId, _sourceFile, final: true);
        return new ParseDelta(CommittedId, result.Events);
    }

    public ParseDelta Complete()
    {
        var first = CommittedId;
        var result = _parser.ParseChunk(_carry, _carryOffset, first, _sourceFile, final: true);
        _carryOffset += _carry.Length;
        _carry = string.Empty;
        CommittedId = first + result.Events.Count;
        return new ParseDelta(first, result.Events);
    }
}
