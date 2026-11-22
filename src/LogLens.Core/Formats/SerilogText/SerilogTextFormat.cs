namespace LogLens.Core.Formats.SerilogText;

/// <summary>
/// Serilog's plain-text file output (SPEC §2 F1). v1.0 reads the default output template only:
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}</c>.
/// </summary>
public sealed class SerilogTextFormat : ILogFormat
{
    private readonly OutputTemplate _template;

    public SerilogTextFormat(OutputTemplate? template = null)
    {
        _template = template ?? OutputTemplate.Default;
    }

    public string Id => "serilog-text";

    public string DisplayName => "Serilog text";

    public LogFields Fields => _template.HasSourceContext ? LogFields.Exception | LogFields.SourceContext : LogFields.Exception;

    public ILogParser CreateParser(string? sourceFile) => new SerilogTextParser(_template, sourceFile);
}

/// <summary>
/// The incremental parser of <see cref="SerilogTextFormat"/>: drives <see cref="PlainTextLogParser"/> chunk by chunk. The text of the event
/// that is still open is carried over and re-read with the next chunk, so no event is ever split between chunks.
/// </summary>
public sealed class SerilogTextParser : ILogParser
{
    private readonly PlainTextLogParser _parser;
    private readonly string? _sourceFile;
    private string _carry = string.Empty;
    private long _carryOffset;

    public SerilogTextParser(OutputTemplate? template = null, string? sourceFile = null)
    {
        _parser = new PlainTextLogParser(template);
        _sourceFile = sourceFile;
    }

    /// <summary>Events with a lower id are final; this id and above are provisional (the event still open).</summary>
    public int CommittedId { get; private set; }

    /// <summary>True when the text held back ends in the middle of a line.</summary>
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
