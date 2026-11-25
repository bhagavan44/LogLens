using System.Text;
using LogLens.Core.Formats.Text;

namespace LogLens.Core.Formats.NLog;

/// <summary>
/// Compiles an NLog layout (<c>${longdate}|${level:uppercase=true}|${logger}|${message}</c>) into the <see cref="LinePattern"/> of the first line
/// of an event. Lines that don't match are continuation lines of the previous event (FR-F3).
/// </summary>
/// <remarks>
/// <para>Dates: <c>${longdate}</c>, <c>${date}</c> (with <c>format=</c>), <c>${shortdate}</c> and <c>${time}</c>, also combined, and
/// <c>universalTime=true</c>. <c>${level}</c>, <c>${message}</c> and <c>${logger}</c> (read as the <c>SourceContext</c> property) have their own
/// meaning; <c>${exception}</c> on the event's line becomes the exception; <c>${newline}</c> ends the first line.</para>
/// <para>Every other renderer becomes a property: <c>${event-properties:item=RequestId}</c>, <c>${mdlc:RequestId}</c> and
/// <c>${scopeproperty:RequestId}</c> are named by their item, <c>${threadid}</c> is <c>ThreadId</c>, unknown renderers are named after themselves.
/// Wrappers (<c>${uppercase:${level}}</c>, <c>${pad:padding=5:inner=…}</c>) read their inner layout; <c>${onexception:…}</c> and
/// <c>${when:…}</c> are optional.</para>
/// </remarks>
public static class NLogLayout
{
    /// <summary>The layout of NLog's file target when none is configured.</summary>
    public const string DefaultText = "${longdate}|${level:uppercase=true}|${logger}|${message:withexception=true}";

    private const string Kind = "NLog layout";

    private static readonly Dictionary<string, string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["threadid"] = "ThreadId",
        ["threadname"] = "ThreadName",
        ["processid"] = "ProcessId",
        ["processname"] = "ProcessName",
        ["machinename"] = "MachineName",
        ["hostname"] = "HostName",
        ["callsite"] = "CallSite",
        ["callsite-linenumber"] = "LineNumber",
        ["callsite-filename"] = "FileName",
        ["stacktrace"] = "StackTrace",
        ["activityid"] = "ActivityId",
        ["activity"] = "Activity",
        ["appdomain"] = "AppDomain",
        ["identity"] = "Identity",
        ["windows-identity"] = "WindowsIdentity",
        ["sequenceid"] = "SequenceId",
        ["counter"] = "Counter",
        ["ticks"] = "Ticks",
        ["ndc"] = "NDC",
        ["ndlc"] = "NDLC",
        ["scopenested"] = "ScopeNested",
        ["all-event-properties"] = "Properties",
        ["event-properties"] = "Properties",
        ["aspnet-request-url"] = "RequestUrl",
        ["aspnet-request-method"] = "RequestMethod",
        ["aspnet-traceidentifier"] = "TraceIdentifier",
    };

    /// <summary>Renderers that name a property through their <c>item</c> option (or their first, unnamed one).</summary>
    private static readonly HashSet<string> ItemRenderers = new(StringComparer.OrdinalIgnoreCase)
    {
        "event-properties", "event-property", "event-context", "mdc", "mdlc", "gdc", "scopeproperty", "environment", "var", "aspnet-item",
    };

    /// <summary>Wrappers whose inner layout is only written sometimes.</summary>
    private static readonly HashSet<string> OptionalWrappers = new(StringComparer.OrdinalIgnoreCase) { "onexception", "when" };

    /// <summary>The default layout, compiled.</summary>
    public static LinePattern Default { get; } = Parse(DefaultText);

    /// <exception cref="FormatException">The layout is malformed or lacks a date or the message.</exception>
    public static LinePattern Parse(string layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var builder = new LinePatternBuilder(Kind);
        Compile(Tokenise(layout), builder, padding: 0);
        return builder.Build();
    }

    private static void Compile(List<Node> nodes, LinePatternBuilder builder, int padding)
    {
        foreach (var node in nodes)
        {
            if (builder.Stopped)
            {
                return;
            }

            if (node.Literal is { } literal)
            {
                builder.Literal(literal);
            }
            else
            {
                Compile(node.Renderer!, builder, padding);
            }
        }
    }

    private static void Compile(Renderer r, LinePatternBuilder builder, int outerPadding)
    {
        var padding = r.Option("padding") is { } p && int.TryParse(p, out var width) ? width : outerPadding;
        var universal = string.Equals(r.Option("universalTime"), "true", StringComparison.OrdinalIgnoreCase);
        var name = r.Name.ToLowerInvariant();

        // A wrapper: read what it wraps.
        var inner = r.Option("inner") ?? (r.Default is { } d && d.Contains("${", StringComparison.Ordinal) ? d : null);
        if (inner is not null && !ItemRenderers.Contains(name))
        {
            var nodes = Tokenise(inner);
            if (OptionalWrappers.Contains(name))
            {
                if (ContainsNewLine(nodes))
                {
                    builder.Stop(); // e.g. ${onexception:${newline}${exception}}: the exception follows on the next lines
                    return;
                }

                builder.BeginOptional();
                Compile(nodes, builder, padding);
                builder.EndOptional();
            }
            else
            {
                Compile(nodes, builder, padding);
            }

            return;
        }

        switch (name)
        {
            case "longdate":
                builder.Timestamp("yyyy-MM-dd HH:mm:ss.ffff", universal);
                break;
            case "date":
                builder.Timestamp(r.Option("format") ?? r.Default ?? "yyyy/MM/dd HH:mm:ss.fff", universal);
                break;
            case "shortdate":
                builder.Timestamp("yyyy-MM-dd", universal);
                break;
            case "time":
                builder.Timestamp("HH:mm:ss.ffff", universal);
                break;
            case "level":
                var format = r.Option("format");
                builder.Level(
                    format?.Equals("FirstCharacter", StringComparison.OrdinalIgnoreCase) == true ? "[A-Za-z]"
                    : format?.Equals("Ordinal", StringComparison.OrdinalIgnoreCase) == true ? @"\d+"
                    : "[A-Za-z]+",
                    padding);
                break;
            case "message":
                builder.Message();
                break;
            case "logger" or "loggername":
                builder.Property(LinePatternBuilder.LoggerProperty);
                break;
            case "exception":
                builder.Exception();
                break;
            case "newline":
                builder.Stop();
                break;
            case "literal":
                builder.Literal(r.Option("text") ?? r.Default ?? string.Empty);
                break;
            default:
                builder.Property(PropertyName(r, name));
                break;
        }
    }

    private static string PropertyName(Renderer r, string name)
    {
        if (ItemRenderers.Contains(name) && (r.Option("item") ?? r.Default) is { Length: > 0 } item)
        {
            return item;
        }

        return KnownProperties.TryGetValue(name, out var known) ? known : LinePatternBuilder.PropertyName(name);
    }

    private static bool ContainsNewLine(List<Node> nodes)
        => nodes.Any(n => n.Renderer is { } r
                          && (r.Name.Equals("newline", StringComparison.OrdinalIgnoreCase)
                              || (r.Option("inner") ?? r.Default) is { } inner && inner.Contains("${", StringComparison.Ordinal) && ContainsNewLine(Tokenise(inner))));

    // ------------------------------------------------------------------ tokenising

    /// <summary>Literal text, or a <c>${…}</c> renderer.</summary>
    private sealed record Node(string? Literal, Renderer? Renderer);

    /// <summary>A renderer: its name, its named options and its first unnamed option (the default parameter).</summary>
    private sealed record Renderer(string Name, Dictionary<string, string> Options, string? Default)
    {
        public string? Option(string key) => Options.TryGetValue(key, out var value) ? value : null;
    }

    private static List<Node> Tokenise(string layout)
    {
        var nodes = new List<Node>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < layout.Length)
        {
            if (layout[i] == '$' && i + 1 < layout.Length && layout[i + 1] == '{')
            {
                if (literal.Length > 0)
                {
                    nodes.Add(new Node(literal.ToString(), null));
                    literal.Clear();
                }

                nodes.Add(new Node(null, ReadRenderer(layout, ref i)));
                continue;
            }

            if (layout[i] == '\\' && i + 1 < layout.Length)
            {
                i++;
            }

            literal.Append(layout[i]);
            i++;
        }

        if (literal.Length > 0)
        {
            nodes.Add(new Node(literal.ToString(), null));
        }

        return nodes;
    }

    /// <summary>Reads <c>${name:option=value:…}</c> starting at <paramref name="i"/> (on the <c>$</c>); leaves <paramref name="i"/> after the closing brace.</summary>
    private static Renderer ReadRenderer(string layout, ref int i)
    {
        var start = i;
        i += 2;
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        while (true)
        {
            if (i >= layout.Length)
            {
                throw new FormatException($"'{layout[start..]}' has no closing '}}'.");
            }

            var c = layout[i];
            if (c == '\\' && i + 1 < layout.Length)
            {
                // At the top level an escape is resolved; inside a nested layout it is kept for when that layout is read.
                if (depth > 0)
                {
                    current.Append(c);
                }

                current.Append(layout[i + 1]);
                i += 2;
                continue;
            }

            if (c == '$' && i + 1 < layout.Length && layout[i + 1] == '{')
            {
                depth++;
                current.Append("${");
                i += 2;
                continue;
            }

            if (c == '}')
            {
                i++;
                if (depth == 0)
                {
                    parts.Add(current.ToString());
                    break;
                }

                depth--;
                current.Append(c);
                continue;
            }

            if (c == ':' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        var name = parts[0].Trim();
        if (name.Length == 0)
        {
            throw new FormatException($"'{layout[start..i]}' has no renderer name.");
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? defaultParameter = null;
        foreach (var part in parts.Skip(1))
        {
            var eq = EqualsAtTopLevel(part);
            if (eq < 0)
            {
                defaultParameter ??= part;
            }
            else
            {
                options[part[..eq].Trim()] = part[(eq + 1)..];
            }
        }

        return new Renderer(name, options, defaultParameter);
    }

    /// <summary>The '=' that separates an option's name from its value (not one inside a nested layout), or -1.</summary>
    private static int EqualsAtTopLevel(string part)
    {
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] == '$' && i + 1 < part.Length && part[i + 1] == '{')
            {
                return -1; // a nested layout before any '=': this is an unnamed option
            }

            if (part[i] == '=')
            {
                return part[..i].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ' ') ? i : -1;
            }
        }

        return -1;
    }
}
