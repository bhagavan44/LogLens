using System.Text.Json;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.Json;

/// <summary>
/// Serilog's compact JSON / CLEF (SPEC §2 F2), as written by <c>CompactJsonFormatter</c> and <c>RenderedCompactJsonFormatter</c>: one JSON object
/// per line with <c>@t</c> (timestamp), <c>@l</c> (level, Information when absent), <c>@m</c> / <c>@mt</c> (message / template), <c>@x</c>
/// (exception), <c>@r</c> (renderings) and <c>@tr</c> / <c>@sp</c> (trace and span ids); every other key is a property, with a leading
/// <c>@@</c> standing for a literal <c>@</c>.
/// </summary>
public sealed class ClefFormat : ILogFormat, IJsonEventReader
{
    public const string FormatId = "clef";

    public string Id => FormatId;

    public string DisplayName => "Serilog CLEF";

    public LogFields Fields => LogFields.Exception | LogFields.MessageTemplate | LogFields.Properties | LogFields.SourceContext;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new JsonLinesParser(this, sourceFile, startOffset, firstId);

    /// <summary>A JSON object with <c>@t</c> and a message, template or exception.</summary>
    public bool Recognises(string line)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return JsonFormatHelpers.TryReadTopLevelNames(line, names)
               && names.Contains("@t")
               && (names.Contains("@m") || names.Contains("@mt") || names.Contains("@x"));
    }

    public bool TryRead(JsonElement root, out JsonEventFields fields)
    {
        fields = default;
        if (!root.TryGetProperty("@t", out var t) || !JsonFormatHelpers.TryParseTimestamp(t, out var timestamp))
        {
            return false;
        }

        var level = LogLevel.Information;
        string? message = null;
        string? template = null;
        string? exception = null;
        List<string?>? renderings = null;
        Dictionary<string, LogValue>? properties = null;

        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            if (name.Length > 1 && name[0] == '@' && name[1] != '@')
            {
                switch (name)
                {
                    case "@l":
                        level = value.ValueKind == JsonValueKind.String ? LogLevelParser.Parse(value.GetString()) : LogLevel.Unknown;
                        break;
                    case "@m":
                        message = JsonFormatHelpers.StringOrNull(value);
                        break;
                    case "@mt":
                        template = JsonFormatHelpers.StringOrNull(value);
                        break;
                    case "@x":
                        exception = JsonFormatHelpers.StringOrNull(value);
                        break;
                    case "@r" when value.ValueKind == JsonValueKind.Array:
                        renderings = [.. value.EnumerateArray().Select(JsonFormatHelpers.StringOrNull)];
                        break;
                    case "@tr":
                        Add(ref properties, "TraceId", LogValue.FromJson(value));
                        break;
                    case "@sp":
                        Add(ref properties, "SpanId", LogValue.FromJson(value));
                        break;
                    case "@i":
                        Add(ref properties, "EventId", LogValue.FromJson(value));
                        break;
                }

                continue;
            }

            // "@@name" is a property called "@name".
            Add(ref properties, name.StartsWith("@@", StringComparison.Ordinal) ? name[1..] : name, LogValue.FromJson(value), overwrite: true);
        }

        message ??= template is null ? string.Empty : MessageTemplateRenderer.Render(template, properties, renderings);
        fields = new JsonEventFields(timestamp, level, message, string.IsNullOrEmpty(exception) ? null : exception.TrimEnd(), template, properties);
        return true;
    }

    private static void Add(ref Dictionary<string, LogValue>? properties, string name, LogValue value, bool overwrite = false)
    {
        properties ??= new Dictionary<string, LogValue>(StringComparer.Ordinal);
        if (overwrite)
        {
            properties[name] = value;
        }
        else
        {
            properties.TryAdd(name, value); // a real property of that name wins over @tr / @sp / @i
        }
    }
}
