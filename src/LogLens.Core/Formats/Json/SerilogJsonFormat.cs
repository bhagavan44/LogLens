using System.Text.Json;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.Json;

/// <summary>
/// Serilog's classic JSON (SPEC §2 F3), as written by <c>JsonFormatter</c>: one object per line with <c>Timestamp</c>, <c>Level</c>,
/// <c>MessageTemplate</c>, optionally <c>RenderedMessage</c> and <c>Exception</c>, a <c>Properties</c> object and optionally
/// <c>Renderings</c> (property name → list of <c>{ Format, Rendering }</c>).
/// </summary>
public sealed class SerilogJsonFormat : ILogFormat, IJsonEventReader
{
    public const string FormatId = "serilog-json";

    public string Id => FormatId;

    public string DisplayName => "Serilog JSON";

    public LogFields Fields => LogFields.Exception | LogFields.MessageTemplate | LogFields.Properties | LogFields.SourceContext;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new JsonLinesParser(this, sourceFile, startOffset, firstId);

    /// <summary>A JSON object with <c>Timestamp</c> and a <c>MessageTemplate</c> or <c>RenderedMessage</c>.</summary>
    public bool Recognises(string line)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return JsonFormatHelpers.TryReadTopLevelNames(line, names)
               && names.Contains("Timestamp")
               && (names.Contains("MessageTemplate") || names.Contains("RenderedMessage"));
    }

    public bool TryRead(JsonElement root, out JsonEventFields fields)
    {
        fields = default;
        if (!root.TryGetProperty("Timestamp", out var t) || !JsonFormatHelpers.TryParseTimestamp(t, out var timestamp))
        {
            return false;
        }

        var level = root.TryGetProperty("Level", out var l) && l.ValueKind == JsonValueKind.String ? LogLevelParser.Parse(l.GetString()) : LogLevel.Information;
        var template = root.TryGetProperty("MessageTemplate", out var mt) ? JsonFormatHelpers.StringOrNull(mt) : null;
        var rendered = root.TryGetProperty("RenderedMessage", out var rm) ? JsonFormatHelpers.StringOrNull(rm) : null;
        var exception = root.TryGetProperty("Exception", out var x) ? JsonFormatHelpers.StringOrNull(x) : null;

        Dictionary<string, LogValue>? properties = null;
        if (root.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            properties = new Dictionary<string, LogValue>(StringComparer.Ordinal);
            foreach (var property in props.EnumerateObject())
            {
                properties[property.Name] = LogValue.FromJson(property.Value);
            }
        }

        var message = rendered ?? (template is null ? string.Empty : MessageTemplateRenderer.Render(template, properties, RenderingsInOrder(root, template)));
        fields = new JsonEventFields(timestamp, level, message, string.IsNullOrEmpty(exception) ? null : exception.TrimEnd(), template, properties);
        return true;
    }

    /// <summary>The <c>Renderings</c> of the template's formatted holes, in the order they appear in the template (what the renderer expects).</summary>
    private static List<string?>? RenderingsInOrder(JsonElement root, string template)
    {
        if (!root.TryGetProperty("Renderings", out var renderings) || renderings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new List<string?>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        while ((i = template.IndexOf('{', i)) >= 0)
        {
            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                break;
            }

            var hole = template[(i + 1)..close].TrimStart('@', '$');
            var colon = hole.IndexOf(':');
            if (colon >= 0)
            {
                var name = hole[..colon];
                var comma = name.IndexOf(',');
                if (comma >= 0)
                {
                    name = name[..comma];
                }

                string? text = null;
                if (renderings.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var n = used.GetValueOrDefault(name);
                    used[name] = n + 1;
                    var k = 0;
                    foreach (var item in list.EnumerateArray())
                    {
                        if (k++ == n && item.TryGetProperty("Rendering", out var r))
                        {
                            text = JsonFormatHelpers.StringOrNull(r);
                        }
                    }
                }

                result.Add(text);
            }

            i = close + 1;
        }

        return result;
    }
}
