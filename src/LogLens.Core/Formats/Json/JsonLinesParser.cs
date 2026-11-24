using System.Text.Json;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.Json;

/// <summary>
/// The incremental parser of a log with one JSON object per line (CLEF, Serilog's classic JSON). Every complete line is one event and final at
/// once; blank lines are skipped, and a line that is not a JSON object of the format becomes an <em>unparsed</em> event (FR-F5). An unterminated
/// last line is held back until its newline arrives (FR-T6).
/// </summary>
/// <remarks>JSON is read with a depth limit and a line-length limit, so hostile input can't exhaust the stack or memory (NFR-10).</remarks>
public sealed class JsonLinesParser : ILogParser
{
    /// <summary>Lines longer than this are not parsed as JSON but kept as unparsed text.</summary>
    public const int MaxLineLength = 4 * 1024 * 1024;

    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64, AllowTrailingCommas = false };

    private readonly IJsonEventReader _reader;
    private readonly string? _sourceFile;
    private string _carry = string.Empty;
    private long _carryOffset;

    public JsonLinesParser(IJsonEventReader reader, string? sourceFile = null, long startOffset = 0, int firstId = 0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(firstId);
        _reader = reader;
        _sourceFile = sourceFile;
        _carryOffset = startOffset;
        CommittedId = firstId;
    }

    public int CommittedId { get; private set; }

    public long EndOffset => _carryOffset + _carry.Length;

    public bool HasPartialLine => _carry.Length > 0;

    public ParseDelta Append(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var work = _carry.Length == 0 ? text : string.Concat(_carry, text);
        var first = CommittedId;
        var events = new List<LogEvent>(Math.Max(4, work.Length / 200));
        var pos = 0;
        var lines = 0;
        while (pos < work.Length)
        {
            var newline = work.IndexOf('\n', pos);
            if (newline < 0)
            {
                break;
            }

            if ((++lines & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            AddLine(work, pos, newline, events);
            pos = newline + 1;
        }

        _carryOffset += pos;
        _carry = pos >= work.Length ? string.Empty : work[pos..];
        CommittedId = first + events.Count;
        return new ParseDelta(first, events);
    }

    public ParseDelta Preview()
    {
        var events = new List<LogEvent>(1);
        AddLine(_carry, 0, _carry.Length, events, firstId: CommittedId);
        return new ParseDelta(CommittedId, events);
    }

    public ParseDelta Complete()
    {
        var delta = Preview();
        _carryOffset += _carry.Length;
        _carry = string.Empty;
        CommittedId += delta.Events.Count;
        return delta;
    }

    private void AddLine(string text, int start, int end, List<LogEvent> events, int? firstId = null)
    {
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        if (text.AsSpan(start, end - start).IsWhiteSpace())
        {
            return;
        }

        var id = (firstId ?? CommittedId) + events.Count;
        var offset = _carryOffset + start;
        var length = end - start;
        var line = text.Substring(start, length);
        events.Add(Parse(line, id, offset) ?? new LogEvent(id, _sourceFile, offset, length, DateTimeOffset.MinValue, LogLevel.Unparsed, line, null));
    }

    private LogEvent? Parse(string line, int id, long offset)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{' || line.Length > MaxLineLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !_reader.TryRead(document.RootElement, out var fields))
            {
                return null;
            }

            return new LogEvent(
                id,
                _sourceFile,
                offset,
                line.Length,
                fields.Timestamp,
                fields.Level,
                fields.Message,
                fields.Exception,
                fields.MessageTemplate,
                fields.Properties);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The common fields of one event read from a JSON object.</summary>
public readonly record struct JsonEventFields(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Exception,
    string? MessageTemplate,
    IReadOnlyDictionary<string, LogValue>? Properties);

/// <summary>Maps one JSON object of a format to the common event fields.</summary>
public interface IJsonEventReader
{
    /// <summary>False when <paramref name="root"/> is not an event of this format (it is then shown as unparsed text).</summary>
    bool TryRead(JsonElement root, out JsonEventFields fields);
}

/// <summary>Helpers shared by the JSON formats.</summary>
internal static class JsonFormatHelpers
{
    /// <summary>
    /// A cheap first look at a line: the top-level property names of a JSON object, read without building a document. False when the line is
    /// not a JSON object.
    /// </summary>
    public static bool TryReadTopLevelNames(string line, HashSet<string> names)
    {
        names.Clear();
        var span = line.AsSpan().Trim();
        if (span.Length < 2 || span[0] != '{' || span[^1] != '}' || line.Length > JsonLinesParser.MaxLineLength)
        {
            return false;
        }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                names.Add(reader.GetString()!);
                reader.Read();
                reader.Skip();
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return element.ValueKind == JsonValueKind.String
               && DateTimeOffset.TryParse(element.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out timestamp);
    }

    public static string? StringOrNull(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
}
