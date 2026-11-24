using LogLens.Core.Models;

namespace LogLens.Core.Formats;

/// <summary>
/// "Detect the format" (FR-F1): its parser looks at the first lines of the text and then reads everything with the format of
/// <see cref="FormatCatalog"/> that recognises them best. A placeholder until then: the document takes the detected format from
/// <see cref="DetectingParser.Detected"/>.
/// </summary>
public sealed class AutoDetectFormat(FormatCatalog catalog) : ILogFormat
{
    public const string FormatId = "auto";

    public FormatCatalog Catalog { get; } = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string Id => FormatId;

    public string DisplayName => "Detecting format…";

    public LogFields Fields => LogFields.Exception;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new DetectingParser(Catalog, sourceFile, startOffset, firstId);

    public bool Recognises(string line) => false;
}

/// <summary>
/// A parser that picks its format from the first complete, non-empty lines it is given (up to <see cref="FormatCatalog.DetectionLines"/>), then
/// hands everything to that format's parser. A file is read in large first chunks, so detection normally sees many lines; a live log that starts
/// empty is detected from its first line. Until a complete line arrives nothing is listed.
/// </summary>
public sealed class DetectingParser : ILogParser
{
    /// <summary>Text held back without a single complete line before detection gives up waiting and uses what it has.</summary>
    private const int MaxBuffer = 1024 * 1024;

    private readonly FormatCatalog _catalog;
    private readonly string? _sourceFile;
    private readonly long _startOffset;
    private readonly int _firstId;
    private string _buffer = string.Empty;
    private ILogParser? _inner;

    public DetectingParser(FormatCatalog catalog, string? sourceFile = null, long startOffset = 0, int firstId = 0)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(firstId);
        _catalog = catalog;
        _sourceFile = sourceFile;
        _startOffset = startOffset;
        _firstId = firstId;
    }

    /// <summary>The format chosen; null until enough text has arrived.</summary>
    public ILogFormat? Detected { get; private set; }

    public int CommittedId => _inner?.CommittedId ?? _firstId;

    public long EndOffset => _inner?.EndOffset ?? _startOffset + _buffer.Length;

    public bool HasPartialLine => _inner?.HasPartialLine ?? (_buffer.Length > 0 && _buffer[^1] != '\n');

    public ParseDelta Append(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_inner is not null)
        {
            return _inner.Append(text, cancellationToken);
        }

        _buffer = _buffer.Length == 0 ? text : string.Concat(_buffer, text);
        var lines = CompleteLines(_buffer);
        if (lines.Count == 0 && _buffer.Length < MaxBuffer)
        {
            return new ParseDelta(_firstId, []);
        }

        return Start(lines.Count > 0 ? lines : [_buffer]).Append(TakeBuffer(), cancellationToken);
    }

    public ParseDelta Preview()
    {
        if (_inner is not null)
        {
            return _inner.Preview();
        }

        // Feeding the held-back text to the chosen parser is what the next Append would do, so the events seen are the same.
        var inner = Start(AllLines(_buffer));
        return Combine(inner.Append(TakeBuffer()), inner.Preview());
    }

    public ParseDelta Complete()
    {
        if (_inner is not null)
        {
            return _inner.Complete();
        }

        var inner = Start(AllLines(_buffer));
        return Combine(inner.Append(TakeBuffer()), inner.Complete());
    }

    private ILogParser Start(IEnumerable<string> sample)
    {
        Detected = _catalog.Detect(sample);
        _inner = Detected.CreateParser(_sourceFile, _startOffset, _firstId);
        return _inner;
    }

    private string TakeBuffer()
    {
        var text = _buffer;
        _buffer = string.Empty;
        return text;
    }

    /// <summary>Two deltas applied one after the other, as one.</summary>
    private static ParseDelta Combine(ParseDelta first, ParseDelta second)
    {
        var events = new List<LogEvent>(first.Events.Count + second.Events.Count);
        events.AddRange(first.Events.Where(e => e.Id < second.FirstId));
        events.AddRange(second.Events);
        return new ParseDelta(Math.Min(first.FirstId, second.FirstId), events);
    }

    /// <summary>The non-empty complete lines of <paramref name="text"/>, at most <see cref="FormatCatalog.DetectionLines"/>.</summary>
    private static List<string> CompleteLines(string text)
    {
        var lines = new List<string>();
        var pos = 0;
        while (lines.Count < FormatCatalog.DetectionLines)
        {
            var newline = text.IndexOf('\n', pos);
            if (newline < 0)
            {
                break;
            }

            var line = text[pos..newline].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }

            pos = newline + 1;
        }

        return lines;
    }

    private static IEnumerable<string> AllLines(string text) => text.Split('\n').Take(FormatCatalog.DetectionLines * 4);
}
