namespace LogLens.Core.Formats.Text;

/// <summary>
/// A plain-text format whose event lines a <see cref="LinePattern"/> describes: NLog, log4net or a user's regular expression (Serilog text has its
/// own <see cref="SerilogText.SerilogTextFormat"/>, which also keeps its output template).
/// </summary>
public sealed class TextLogFormat : ILogFormat
{
    private static readonly char[] CrLf = ['\r', '\n'];

    private readonly PlainTextLogParser _probe;

    public TextLogFormat(string id, string displayName, LinePattern pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(pattern);
        Id = id;
        DisplayName = displayName;
        Pattern = pattern;
        _probe = new PlainTextLogParser(pattern);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public LinePattern Pattern { get; }

    public LogFields Fields => Pattern.Fields;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new TextLogParser(Pattern, sourceFile, startOffset, firstId);

    /// <summary>The line is the first line of an event of this format.</summary>
    public bool Recognises(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _probe.IsEventStart(line.TrimEnd(CrLf));
    }
}
