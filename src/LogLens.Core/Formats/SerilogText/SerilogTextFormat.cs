namespace LogLens.Core.Formats.SerilogText;

/// <summary>
/// Serilog's plain-text file output (SPEC §2 F1) written with an <see cref="OutputTemplate"/>: by default the file sink's
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}</c>, or a custom template from the settings
/// (FR-F2), whose property tokens become properties of the events.
/// </summary>
public sealed class SerilogTextFormat : ILogFormat
{
    public const string DefaultId = "serilog-text";

    /// <summary>Custom templates have ids made of this prefix and their name.</summary>
    public const string CustomIdPrefix = "template:";

    private static readonly char[] CrLf = ['\r', '\n'];

    private readonly PlainTextLogParser _probe;

    /// <param name="template">The output template; the default one when null.</param>
    /// <param name="name">A custom template's name, shown as the format's name; null for the built-in default.</param>
    public SerilogTextFormat(OutputTemplate? template = null, string? name = null)
    {
        Template = template ?? OutputTemplate.Default;
        _probe = new PlainTextLogParser(Template);
        Id = name is null ? DefaultId : CustomIdPrefix + name;
        DisplayName = name ?? "Serilog text";
    }

    public OutputTemplate Template { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public LogFields Fields => LogFields.Exception
                               | (Template.PropertyNames.Count > 0 ? LogFields.Properties : LogFields.None)
                               | (Template.HasSourceContext ? LogFields.SourceContext : LogFields.None);

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new SerilogTextParser(Template, sourceFile, startOffset, firstId);

    /// <summary>The line is the first line of an event written with this template.</summary>
    public bool Recognises(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _probe.IsEventStart(line.TrimEnd(CrLf));
    }
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

    public SerilogTextParser(OutputTemplate? template = null, string? sourceFile = null, long startOffset = 0, int firstId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(firstId);
        _parser = new PlainTextLogParser(template);
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
