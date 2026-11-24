using System.Globalization;
using System.Text;
using LogLens.Core.Models;

namespace LogLens.Core.Formats;

/// <summary>
/// Renders a Serilog message template (<c>Order {OrderId} shipped in {Elapsed:0.0} ms</c>) with an event's properties, the way Serilog's own
/// text output does: text values in double quotes unless the hole has the <c>:l</c> format, numbers formatted in the invariant culture,
/// <c>{@Structure}</c> and sequences in Serilog's structured notation, <c>:j</c> as JSON, and alignment (<c>{Name,-10}</c>) honoured. A hole
/// whose property is missing is kept as written. Used when a structured log stores the template but not the rendered message (CLEF without
/// <c>@m</c>, classic JSON without <c>RenderedMessage</c>).
/// </summary>
public static class MessageTemplateRenderer
{
    /// <param name="template">The message template.</param>
    /// <param name="properties">The event's properties.</param>
    /// <param name="renderings">
    /// Pre-rendered text for the holes that have a format, in the order they appear (CLEF <c>@r</c>); used instead of formatting the value here,
    /// since the log's own culture and types (dates written as text) can't be reproduced.
    /// </param>
    public static string Render(string template, IReadOnlyDictionary<string, LogValue>? properties, IReadOnlyList<string?>? renderings = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.IndexOf('{') < 0 && template.IndexOf('}') < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 32);
        var formatted = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i++;
                    continue;
                }

                if (TryReadHole(template, i, out var hole, out var end))
                {
                    var rendering = hole.Format is not null && renderings is not null && formatted < renderings.Count ? renderings[formatted] : null;
                    if (hole.Format is not null)
                    {
                        formatted++;
                    }

                    if (properties is not null && properties.TryGetValue(hole.Name, out var value))
                    {
                        Append(sb, rendering ?? RenderValue(value, hole), hole.Alignment);
                    }
                    else
                    {
                        sb.Append(template, i, end - i + 1);
                    }

                    i = end;
                    continue;
                }

                sb.Append(c);
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>A value as Serilog writes it in a message: text in quotes, structures as <c>Type { A: 1 }</c>, sequences as <c>[1, 2]</c>.</summary>
    public static string RenderValue(LogValue value, string? format = null)
        => RenderValue(value, new Hole(string.Empty, format, 0, Destructure: false));

    private static string RenderValue(LogValue value, Hole hole)
    {
        if (hole.Format == "j")
        {
            return value.ToJson();
        }

        var sb = new StringBuilder();
        Write(sb, value, hole.Format, topLevel: true);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, LogValue value, string? format, bool topLevel)
    {
        switch (value.Value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                if (topLevel && format is not null && format.Contains('l', StringComparison.Ordinal))
                {
                    sb.Append(s);
                }
                else
                {
                    LogValue.WriteString(sb, s);
                }

                break;
            case bool b:
                sb.Append(b ? "True" : "False"); // .NET's Boolean.ToString, as Serilog writes it
                break;
            case IFormattable number when value.IsNumber:
                try
                {
                    sb.Append(number.ToString(topLevel && format is not ("l" or "") ? format : null, CultureInfo.InvariantCulture));
                }
                catch (FormatException)
                {
                    sb.Append(number.ToString(null, CultureInfo.InvariantCulture));
                }

                break;
            case IReadOnlyList<LogValue> list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, list[i], null, topLevel: false);
                }

                sb.Append(']');
                break;
            case IReadOnlyDictionary<string, LogValue> map:
                var typeTag = map.TryGetValue("$type", out var t) ? t.Value as string : map.TryGetValue("_typeTag", out var tt) ? tt.Value as string : null;
                if (typeTag is not null)
                {
                    sb.Append(typeTag).Append(' ');
                }

                sb.Append('{');
                var first = true;
                foreach (var (key, item) in map)
                {
                    if (key is "$type" or "_typeTag")
                    {
                        continue;
                    }

                    sb.Append(first ? " " : ", ");
                    first = false;
                    sb.Append(key).Append(": ");
                    Write(sb, item, null, topLevel: false);
                }

                sb.Append(first ? "}" : " }");
                break;
            default:
                sb.Append(value);
                break;
        }
    }

    private static void Append(StringBuilder sb, string text, int alignment)
    {
        var pad = Math.Abs(alignment) - text.Length;
        if (pad <= 0)
        {
            sb.Append(text);
        }
        else if (alignment > 0)
        {
            sb.Append(' ', pad).Append(text);
        }
        else
        {
            sb.Append(text).Append(' ', pad);
        }
    }

    private readonly record struct Hole(string Name, string? Format, int Alignment, bool Destructure);

    /// <summary>Reads <c>{[@|$]Name[,alignment][:format]}</c> starting at <paramref name="start"/>; false when it isn't a valid hole.</summary>
    private static bool TryReadHole(string template, int start, out Hole hole, out int end)
    {
        hole = default;
        end = template.IndexOf('}', start + 1);
        if (end < 0)
        {
            return false;
        }

        var i = start + 1;
        var destructure = false;
        if (i < end && template[i] is '@' or '$')
        {
            destructure = template[i] == '@';
            i++;
        }

        var nameStart = i;
        while (i < end && (char.IsLetterOrDigit(template[i]) || template[i] == '_'))
        {
            i++;
        }

        if (i == nameStart)
        {
            return false;
        }

        var name = template[nameStart..i];
        var alignment = 0;
        if (i < end && template[i] == ',')
        {
            var alignStart = ++i;
            if (i < end && template[i] == '-')
            {
                i++;
            }

            while (i < end && char.IsDigit(template[i]))
            {
                i++;
            }

            if (!int.TryParse(template.AsSpan(alignStart, i - alignStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out alignment))
            {
                return false;
            }
        }

        string? format = null;
        if (i < end && template[i] == ':')
        {
            format = template[(i + 1)..end];
            i = end;
        }

        if (i != end)
        {
            return false;
        }

        hole = new Hole(name, format, alignment, destructure);
        return true;
    }
}
