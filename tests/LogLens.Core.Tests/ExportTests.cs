using System.Text;
using System.Text.Json;
using LogLens.Core.Export;
using LogLens.Core.Formats;
using LogLens.Core.Merging;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

/// <summary>Export writers (FR-E1).</summary>
public class ExportTests
{
    // Text before the first event is kept as an unparsed line (event 1).
    private const string Text =
        "not a log line\n" +
        "2026-11-13 08:00:00.012 +05:30 [INF] Started, \"quoted\" and = fine\n" +
        "2026-11-13 08:00:05.301 +05:30 [ERR] =cmd|' /C calc'!A0\n" +
        "System.InvalidOperationException: boom <b>\n" +
        "   at A.B()\n";

    private static readonly ExportOptions Options = new() { Title = "app.txt", Description = "3 events", GeneratedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero) };

    private static string Export(ExportFormat format, LogDocument? document = null, IEnumerable<LogEvent>? events = null)
    {
        document ??= TestDocuments.FromText(Text);
        var writer = new StringWriter();
        LogExporter.Write(format, events ?? document.Events, document, writer, Options);
        return writer.ToString();
    }

    [Fact]
    public void Every_format_has_a_distinct_extension_and_a_name()
    {
        var formats = Enum.GetValues<ExportFormat>();

        Assert.Equal(formats.Length, formats.Select(LogExporter.FileExtension).Distinct().Count());
        Assert.All(formats, f => Assert.False(string.IsNullOrWhiteSpace(LogExporter.DisplayName(f))));
    }

    [Fact]
    public void Only_a_csv_starts_with_a_byte_order_mark()
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], LogExporter.EncodingFor(ExportFormat.Csv).GetPreamble());
        Assert.Empty(LogExporter.EncodingFor(ExportFormat.Json).GetPreamble());
        Assert.Empty(LogExporter.EncodingFor(ExportFormat.Html).GetPreamble());
    }

    // ------------------------------------------------------------------ CSV

    [Fact]
    public void Csv_quotes_fields_and_guards_against_spreadsheet_formulas()
    {
        var lines = Export(ExportFormat.Csv).Split("\r\n");

        Assert.Equal("Event,Timestamp,Level,Message,Exception,Source", lines[0]);
        Assert.Equal("1,,Unparsed,not a log line,,x.txt", lines[1]);
        Assert.Equal("2,2026-11-13T08:00:00.012+05:30,Information,\"Started, \"\"quoted\"\" and = fine\",,x.txt", lines[2]);
        Assert.StartsWith("3,2026-11-13T08:00:05.301+05:30,Error,'=cmd|' /C calc'!A0,", lines[3]);
    }

    [Fact]
    public void Csv_keeps_multi_line_exceptions_in_one_quoted_cell()
    {
        var csv = Export(ExportFormat.Csv);

        Assert.Contains("\"System.InvalidOperationException: boom <b>\n   at A.B()\"", csv);
    }

    [Fact]
    public void The_formula_guard_can_be_switched_off()
    {
        var document = TestDocuments.FromText(Text);
        var writer = new StringWriter();

        LogExporter.Write(ExportFormat.Csv, document.Events, document, writer, new ExportOptions { SpreadsheetSafe = false });

        Assert.Contains(",=cmd|' /C calc'!A0,", writer.ToString());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    public void Csv_cells(string value, string expected) => Assert.Equal(expected, LogExporter.Csv(value, spreadsheetSafe: true));

    // ------------------------------------------------------------------ JSON lines

    [Fact]
    public void Json_lines_hold_one_parseable_object_per_event()
    {
        var lines = Export(ExportFormat.Json).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        using var error = JsonDocument.Parse(lines[2]);
        var root = error.RootElement;
        Assert.Equal("2026-11-13T08:00:05.301+05:30", root.GetProperty("@t").GetString());
        Assert.Equal("Error", root.GetProperty("@l").GetString());
        Assert.StartsWith("System.InvalidOperationException: boom", root.GetProperty("@x").GetString());
        Assert.Equal(3, root.GetProperty("EventNumber").GetInt32());
        Assert.Equal("x.txt", root.GetProperty("SourceFile").GetString());
    }

    [Fact]
    public void An_unparsed_line_is_marked_in_json_and_has_no_time_or_level()
    {
        var first = JsonDocument.Parse(Export(ExportFormat.Json).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement;

        Assert.True(first.GetProperty("Unparsed").GetBoolean());
        Assert.False(first.TryGetProperty("@t", out _));
        Assert.False(first.TryGetProperty("@l", out _));
        Assert.Equal("not a log line", first.GetProperty("@m").GetString());
    }

    // ------------------------------------------------------------------ original lines

    [Fact]
    public void Text_export_writes_the_original_lines_of_each_event()
    {
        var text = Export(ExportFormat.Text);

        Assert.Equal(
            "not a log line\r\n" +
            "2026-11-13 08:00:00.012 +05:30 [INF] Started, \"quoted\" and = fine\r\n" +
            "2026-11-13 08:00:05.301 +05:30 [ERR] =cmd|' /C calc'!A0\nSystem.InvalidOperationException: boom <b>\n   at A.B()\r\n",
            text);
    }

    // ------------------------------------------------------------------ HTML

    [Fact]
    public void Html_is_self_contained_escaped_and_locked_down()
    {
        var html = Export(ExportFormat.Html);

        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("<title>app.txt</title>", html);
        Assert.Contains("3 events · exported 2026-11-14 09:00:00 +00:00 by LogLens", html);
        Assert.Contains("boom &lt;b&gt;", html); // log text never becomes markup
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("<span class=\"lv Error\">ERR</span>", html);
        Assert.Contains("<tr class=\"Unparsed\">", html);
        Assert.DoesNotContain("<th>Source</th>", html);
    }

    [Fact]
    public void Html_escapes_the_title()
    {
        var document = TestDocuments.FromText(Text);
        var writer = new StringWriter();

        LogExporter.Write(ExportFormat.Html, document.Events, document, writer, new ExportOptions { Title = "<img src=x onerror=alert(1)>" });

        Assert.DoesNotContain("<img", writer.ToString());
    }

    // ------------------------------------------------------------------ Markdown

    [Fact]
    public void Markdown_is_a_table_with_pipes_and_line_breaks_escaped()
    {
        var markdown = Export(ExportFormat.Markdown);
        var lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("| # | Time | Level | Message |", lines[0]);
        Assert.Equal("| ---: | --- | --- | --- |", lines[1]);
        Assert.Equal("| 1 |  | — | not a log line |", lines[2]);
        Assert.Equal("| 3 | 2026-11-13T08:00:05.301+05:30 | ERR | =cmd\\|' /C calc'!A0<br>System.InvalidOperationException: boom <b> |", lines[4]);
    }

    [Fact]
    public void A_merged_log_adds_a_source_column()
    {
        var one = TestDocuments.FromText("2026-11-13 08:00:00.000 +00:00 [INF] a1\n", @"C:\logs\one.txt");
        var two = TestDocuments.FromText("2026-11-13 08:00:05.000 +00:00 [INF] b1\n", @"C:\logs\two.txt");
        var merged = LogMerger.Merge([one, two]);
        var document = new LogDocument(null, LogFormats.SerilogText);
        document.AddMergedFiles(merged.Files);
        document.Text.Append(merged.Text);
        document.Apply(new ParseDelta(0, merged.Events));

        Assert.Contains("<th>Source</th>", Export(ExportFormat.Html, document));
        Assert.Contains("<td class=\"s\">two.txt</td>", Export(ExportFormat.Html, document));
        Assert.StartsWith("| # | Time | Level | Message | Source |", Export(ExportFormat.Markdown, document));
        Assert.Contains("one.txt", Export(ExportFormat.Csv, document));
    }

    [Fact]
    public void Exporting_stops_when_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var document = TestDocuments.FromText(Text);

        Assert.Throws<OperationCanceledException>(() => LogExporter.Write(ExportFormat.Csv, document.Events, document, new StringWriter(), Options, cancellation.Token));
    }

    [Fact]
    public void A_stream_export_through_the_encoding_round_trips_non_ascii_text()
    {
        var document = TestDocuments.FromText("2026-11-13 08:00:00.000 +00:00 [INF] Zażółć gęślą jaźń ✓\n");
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, LogExporter.EncodingFor(ExportFormat.Csv), leaveOpen: true))
        {
            LogExporter.Write(ExportFormat.Csv, document.Events, document, writer, Options);
        }

        Assert.Contains("Zażółć gęślą jaźń ✓", new UTF8Encoding(false).GetString(stream.ToArray()));
    }
}
