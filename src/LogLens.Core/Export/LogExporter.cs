using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LogLens.Core.Models;

namespace LogLens.Core.Export;

public enum ExportFormat
{
    Csv,

    /// <summary>The original lines exactly as they are in the log file.</summary>
    Text,

    /// <summary>One JSON object per line.</summary>
    Json,

    /// <summary>A self-contained, coloured page.</summary>
    Html,

    /// <summary>A Markdown table, for pasting into tickets.</summary>
    Markdown,
}

public sealed record ExportOptions
{
    /// <summary>Heading of an HTML export.</summary>
    public string Title { get; init; } = "LogLens export";

    /// <summary>A line under the heading, e.g. "5 of 120 events (filtered)".</summary>
    public string? Description { get; init; }

    /// <summary>
    /// CSV only: a cell that starts with <c>= + - @</c> (or a tab / CR) is prefixed with an apostrophe so a spreadsheet never evaluates log
    /// text as a formula. Logs from other people are untrusted input (NFR-10).
    /// </summary>
    public bool SpreadsheetSafe { get; init; } = true;

    /// <summary>Stamped into an HTML export; defaults to now.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }
}

/// <summary>Writes events to a <see cref="TextWriter"/> as CSV, plain text, JSON lines, HTML or a Markdown table (FR-E1).</summary>
public static class LogExporter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public static string FileExtension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Text => ".txt",
        ExportFormat.Json => ".jsonl",
        ExportFormat.Html => ".html",
        _ => ".md",
    };

    public static string DisplayName(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "CSV",
        ExportFormat.Text => "Original lines (text)",
        ExportFormat.Json => "JSON lines",
        ExportFormat.Html => "HTML page",
        _ => "Markdown table",
    };

    /// <summary>A UTF-8 encoding; only a CSV gets a byte-order mark, because Excel needs one to read accented characters correctly.</summary>
    public static Encoding EncodingFor(ExportFormat format) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ExportFormat.Csv);

    /// <summary>Writes <paramref name="events"/> (which belong to <paramref name="document"/>, so their raw text can be read) in <paramref name="format"/>.</summary>
    public static void Write(
        ExportFormat format,
        IEnumerable<LogEvent> events,
        LogDocument document,
        TextWriter writer,
        ExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= new ExportOptions();

        switch (format)
        {
            case ExportFormat.Csv:
                WriteCsv(events, document, writer, options, cancellationToken);
                break;
            case ExportFormat.Text:
                WriteText(events, document, writer, cancellationToken);
                break;
            case ExportFormat.Json:
                WriteJson(events, document, writer, cancellationToken);
                break;
            case ExportFormat.Html:
                WriteHtml(events, document, writer, options, cancellationToken);
                break;
            default:
                WriteMarkdown(events, document, writer, cancellationToken);
                break;
        }

        writer.Flush();
    }

    // ------------------------------------------------------------------ shared

    // The timestamp as logged, with its own offset, in a form every tool reads.
    private static string TimeText(LogEvent e) => e.IsUnparsed ? string.Empty : e.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static string LevelName(LogEvent e) => e.Level switch
    {
        LogLevel.Unparsed => "Unparsed",
        LogLevel.Unknown => "Unknown",
        _ => e.Level.ToString(),
    };

    private static string SourceName(LogEvent e, LogDocument document)
        => Path.GetFileName(e.SourceFile ?? document.FilePath ?? string.Empty);

    private static string EventNumber(LogEvent e) => (e.Id + 1).ToString(CultureInfo.InvariantCulture);

    private static void Check(int index, CancellationToken token)
    {
        if ((index & 0x3FF) == 0)
        {
            token.ThrowIfCancellationRequested();
        }
    }

    // ------------------------------------------------------------------ CSV (RFC 4180)

    private static void WriteCsv(IEnumerable<LogEvent> events, LogDocument document, TextWriter w, ExportOptions options, CancellationToken token)
    {
        w.Write("Event,Timestamp,Level,Message,Exception,Source\r\n");
        var n = 0;
        foreach (var e in events)
        {
            Check(n++, token);
            w.Write(EventNumber(e));
            w.Write(',');
            w.Write(Csv(TimeText(e), false));
            w.Write(',');
            w.Write(Csv(LevelName(e), false));
            w.Write(',');
            w.Write(Csv(e.Message, options.SpreadsheetSafe));
            w.Write(',');
            w.Write(Csv(e.Exception ?? string.Empty, options.SpreadsheetSafe));
            w.Write(',');
            w.Write(Csv(SourceName(e, document), options.SpreadsheetSafe));
            w.Write("\r\n");
        }
    }

    internal static string Csv(string value, bool spreadsheetSafe)
    {
        if (spreadsheetSafe && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    // ------------------------------------------------------------------ original lines

    private static void WriteText(IEnumerable<LogEvent> events, LogDocument document, TextWriter w, CancellationToken token)
    {
        var n = 0;
        foreach (var e in events)
        {
            Check(n++, token);
            w.Write(document.GetRaw(e));
            w.Write("\r\n");
        }
    }

    // ------------------------------------------------------------------ JSON lines

    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private static void WriteValue(Utf8JsonWriter json, LogValue value)
    {
        switch (value.Value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string s:
                json.WriteStringValue(s);
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case long l:
                json.WriteNumberValue(l);
                break;
            case decimal m:
                json.WriteNumberValue(m);
                break;
            case double d when double.IsFinite(d):
                json.WriteNumberValue(d);
                break;
            case double d:
                json.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case IReadOnlyList<LogValue> list:
                json.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(json, item);
                }

                json.WriteEndArray();
                break;
            case IReadOnlyDictionary<string, LogValue> map:
                json.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    json.WritePropertyName(key);
                    WriteValue(json, item);
                }

                json.WriteEndObject();
                break;
            default:
                json.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteJson(IEnumerable<LogEvent> events, LogDocument document, TextWriter w, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using var json = new Utf8JsonWriter(buffer, JsonOptions);
        var n = 0;
        foreach (var e in events)
        {
            Check(n++, token);
            buffer.ResetWrittenCount();
            json.Reset(buffer);

            json.WriteStartObject();
            if (e.IsUnparsed)
            {
                json.WriteBoolean("Unparsed", true);
            }
            else
            {
                json.WriteString("@t", TimeText(e));
                json.WriteString("@l", LevelName(e));
            }

            json.WriteString("@m", e.Message);
            if (e.MessageTemplate is { } template)
            {
                json.WriteString("@mt", template);
            }

            if (e.Exception is { } exception)
            {
                json.WriteString("@x", exception);
            }

            if (e.Properties is { } properties)
            {
                // CLEF style: properties at the top level, a leading @ doubled so it can't be read as a reserved field.
                foreach (var (name, value) in properties)
                {
                    if (name is "SourceFile" or "EventNumber")
                    {
                        continue;
                    }

                    json.WritePropertyName(name.StartsWith('@') ? "@" + name : name);
                    WriteValue(json, value);
                }
            }

            var source = SourceName(e, document);
            if (source.Length > 0)
            {
                json.WriteString("SourceFile", source);
            }

            json.WriteNumber("EventNumber", e.Id + 1);
            json.WriteEndObject();
            json.Flush();

            w.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
            w.Write('\n');
        }
    }

    // ------------------------------------------------------------------ HTML

    private const string HtmlStyle = """
        :root{color-scheme:light dark;--bg:#fff;--fg:#1b1b1b;--muted:#5d5d5d;--line:#e0e0e0;--stripe:#f6f6f6;--ex:#fdf3f3}
        @media (prefers-color-scheme:dark){:root{--bg:#1e1e1e;--fg:#e6e6e6;--muted:#a0a0a0;--line:#3a3a3a;--stripe:#252525;--ex:#2c2222}}
        body{margin:0;padding:24px;background:var(--bg);color:var(--fg);font:14px/1.4 "Segoe UI",system-ui,sans-serif}
        h1{font-size:20px;margin:0 0 4px}p.meta{margin:0 0 16px;color:var(--muted)}
        table{border-collapse:collapse;width:100%}th,td{text-align:left;vertical-align:top;padding:4px 10px;border-bottom:1px solid var(--line)}
        th{position:sticky;top:0;background:var(--bg)}tr:nth-child(even) td{background:var(--stripe)}
        td.n,td.t,td.l,td.s{font-family:Consolas,"Cascadia Mono",monospace;font-size:12px;white-space:nowrap}
        td.m{font-family:Consolas,"Cascadia Mono",monospace;font-size:12px;white-space:pre-wrap;word-break:break-word}
        td.m .x{display:block;margin-top:4px;padding:4px 8px;background:var(--ex);border-left:3px solid #e53935;color:var(--muted)}
        .lv{display:inline-block;min-width:3em;padding:0 6px;border-radius:3px;text-align:center;color:#fff;font-weight:600}
        .Verbose{background:#6b6b6b}.Debug{background:#4f6a78}.Information{background:#1565c0}.Warning{background:#8a5a00}
        .Error{background:#c62828}.Fatal{background:#7b1043}.Unknown,.Unparsed{background:#757575}
        tr.Error td.m,tr.Fatal td.m{font-weight:600}
        """;

    private static string LevelBadge(LogEvent e) => LogText.LevelAbbreviation(e.Level);

    private static void WriteHtml(IEnumerable<LogEvent> events, LogDocument document, TextWriter w, ExportOptions options, CancellationToken token)
    {
        var showSource = document.IsMerged;
        var generated = (options.GeneratedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        // The page is self-contained and, with this policy, runs nothing and loads nothing even if a log line contained markup.
        w.Write("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n");
        w.Write("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n");
        w.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>");
        w.Write(WebUtility.HtmlEncode(options.Title));
        w.Write("</title>\n<style>\n");
        w.Write(HtmlStyle);
        w.Write("\n</style></head><body>\n<h1>");
        w.Write(WebUtility.HtmlEncode(options.Title));
        w.Write("</h1>\n<p class=\"meta\">");
        if (!string.IsNullOrEmpty(options.Description))
        {
            w.Write(WebUtility.HtmlEncode(options.Description));
            w.Write(" · ");
        }

        w.Write("exported ");
        w.Write(WebUtility.HtmlEncode(generated));
        w.Write(" by LogLens</p>\n<table>\n<thead><tr><th>#</th><th>Time</th><th>Level</th><th>Message</th>");
        w.Write(showSource ? "<th>Source</th>" : string.Empty);
        w.Write("</tr></thead>\n<tbody>\n");

        var n = 0;
        foreach (var e in events)
        {
            Check(n++, token);
            var level = LevelName(e);
            w.Write("<tr class=\"");
            w.Write(level);
            w.Write("\"><td class=\"n\">");
            w.Write(EventNumber(e));
            w.Write("</td><td class=\"t\">");
            w.Write(WebUtility.HtmlEncode(TimeText(e)));
            w.Write("</td><td class=\"l\"><span class=\"lv ");
            w.Write(level);
            w.Write("\">");
            w.Write(LevelBadge(e));
            w.Write("</span></td><td class=\"m\">");
            w.Write(WebUtility.HtmlEncode(e.Message));
            if (e.Exception is { } exception)
            {
                w.Write("<span class=\"x\">");
                w.Write(WebUtility.HtmlEncode(exception));
                w.Write("</span>");
            }

            w.Write("</td>");
            if (showSource)
            {
                w.Write("<td class=\"s\">");
                w.Write(WebUtility.HtmlEncode(SourceName(e, document)));
                w.Write("</td>");
            }

            w.Write("</tr>\n");
        }

        w.Write("</tbody></table>\n</body></html>\n");
    }

    // ------------------------------------------------------------------ Markdown table

    private static void WriteMarkdown(IEnumerable<LogEvent> events, LogDocument document, TextWriter w, CancellationToken token)
    {
        var showSource = document.IsMerged;
        w.Write(showSource ? "| # | Time | Level | Message | Source |\n| ---: | --- | --- | --- | --- |\n" : "| # | Time | Level | Message |\n| ---: | --- | --- | --- |\n");

        var n = 0;
        foreach (var e in events)
        {
            Check(n++, token);
            var message = e.Message;
            if (e.Exception is { } exception)
            {
                message += "\n" + LogText.FirstLine(exception);
            }

            w.Write("| ");
            w.Write(EventNumber(e));
            w.Write(" | ");
            w.Write(MarkdownCell(TimeText(e)));
            w.Write(" | ");
            w.Write(LevelBadge(e));
            w.Write(" | ");
            w.Write(MarkdownCell(message));
            if (showSource)
            {
                w.Write(" | ");
                w.Write(MarkdownCell(SourceName(e, document)));
            }

            w.Write(" |\n");
        }
    }

    internal static string MarkdownCell(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("|", "\\|", StringComparison.Ordinal)
               .Replace("\r\n", "<br>", StringComparison.Ordinal)
               .Replace("\n", "<br>", StringComparison.Ordinal)
               .Replace("\r", "<br>", StringComparison.Ordinal);
}
