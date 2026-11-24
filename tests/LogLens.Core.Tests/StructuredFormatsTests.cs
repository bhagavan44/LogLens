using System.Text.Json;
using LogLens.Core.Formats;
using LogLens.Core.Formats.Json;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class StructuredFormatsTests
{
    private static List<LogEvent> ParseAll(ILogFormat format, string text, string? path = "x.clef")
    {
        var document = new LogDocument(path, format);
        var parser = format.CreateParser(path);
        document.Text.Append(text);
        document.Apply(parser.Append(text));
        document.Apply(parser.Complete());
        return [.. document.Events];
    }

    private static string SampleText(string folder, string file)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", folder, file));

    // ------------------------------------------------------------------ CLEF

    [Fact]
    public void Clef_line_maps_to_the_common_model()
    {
        const string line = """{"@t":"2026-11-13T04:45:32.123Z","@mt":"Order {Id} shipped in {Elapsed} ms","@l":"Warning","Id":42,"Elapsed":12.5,"Customer":"Ann","SourceContext":"Shop.Orders"}""";

        var e = Assert.Single(ParseAll(LogFormats.Clef, line));

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 4, 45, 32, 123, TimeSpan.Zero), e.Timestamp);
        Assert.Equal(LogLevel.Warning, e.Level);
        Assert.Equal("Order 42 shipped in 12.5 ms", e.Message);
        Assert.Equal("Order {Id} shipped in {Elapsed} ms", e.MessageTemplate);
        Assert.Null(e.Exception);
        Assert.Equal(42L, e.Properties!["Id"].Value);
        Assert.Equal(12.5m, e.Properties["Elapsed"].Value);
        Assert.Equal("Ann", e.Properties["Customer"].Value);
        Assert.Equal("Shop.Orders", e.SourceContext);
        Assert.Equal(0, e.Offset);
        Assert.Equal(line.Length, e.Length);
    }

    [Fact]
    public void Clef_defaults_to_information_and_prefers_the_rendered_message()
    {
        var e = Assert.Single(ParseAll(LogFormats.Clef, """{"@t":"2026-11-13T04:45:32Z","@m":"Rendered text","@mt":"Template {X}","X":1}"""));

        Assert.Equal(LogLevel.Information, e.Level);
        Assert.Equal("Rendered text", e.Message);
        Assert.Equal("Template {X}", e.MessageTemplate);
    }

    [Fact]
    public void Clef_reserved_fields_and_escaped_names()
    {
        const string line = """{"@t":"2026-11-13T04:45:32Z","@mt":"x","@x":"System.Exception: boom\r\n   at A.B()\r\n","@tr":"abc","@sp":"def","@i":"1234abcd","@@weird":"kept","@r":["1"]}""";

        var e = Assert.Single(ParseAll(LogFormats.Clef, line));

        Assert.Equal("System.Exception: boom\r\n   at A.B()", e.Exception);
        Assert.Equal("abc", e.Property("TraceId")!.Value);
        Assert.Equal("def", e.Property("SpanId")!.Value);
        Assert.Equal("1234abcd", e.Property("EventId")!.Value);
        Assert.Equal("kept", e.Property("@weird")!.Value);
        Assert.Null(e.Property("@r"));
    }

    [Fact]
    public void A_real_property_wins_over_the_trace_id_alias()
    {
        var e = Assert.Single(ParseAll(LogFormats.Clef, """{"TraceId":"mine","@t":"2026-11-13T04:45:32Z","@mt":"x","@tr":"abc"}"""));

        Assert.Equal("mine", e.Property("TraceId")!.Value);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"@t\":\"2026-11-13T04:45:32Z\",\"@mt\":\"x\"")] // truncated
    [InlineData("{\"@mt\":\"no timestamp\"}")]
    [InlineData("{\"@t\":\"yesterday\",\"@mt\":\"x\"}")]
    [InlineData("[1, 2, 3]")]
    public void Lines_that_are_not_clef_events_are_kept_as_unparsed(string line)
    {
        var e = Assert.Single(ParseAll(LogFormats.Clef, line));

        Assert.True(e.IsUnparsed);
        Assert.Equal(line, e.Message);
        Assert.Equal(DateTimeOffset.MinValue, e.Timestamp);
    }

    [Fact]
    public void Deeply_nested_json_does_not_crash_the_parser()
    {
        var line = "{\"@t\":\"2026-11-13T04:45:32Z\",\"@mt\":\"x\",\"A\":" + new string('[', 500) + new string(']', 500) + "}";

        var e = Assert.Single(ParseAll(LogFormats.Clef, line));

        Assert.True(e.IsUnparsed); // NFR-10: past the depth limit the line is text, not an event
    }

    [Fact]
    public void Clef_sample_file_parses_as_documented()
    {
        var events = ParseAll(LogFormats.Clef, SampleText("serilog-clef", "log20261113.clef"));

        Assert.Equal(12, events.Count); // 11 events and the line that is not JSON; the blank line is skipped
        Assert.Single(events, e => e.IsUnparsed);
        Assert.Equal("HTTP \"GET\" \"/api/orders/32131\" responded 200 in 810.4123 ms", events[3].Message); // @r supplies the formatted hole
        Assert.Equal("Basket Basket { Id: \"b-9917\", Items: [\"SKU-1\", \"SKU-7\"], Total: 42.5 } checked out", events[8].Message);
        Assert.Equal("Order 93346 shipped to customer 2468", events[9].Message);
        Assert.Equal(LogLevel.Fatal, events[^1].Level);
        Assert.All(events.Where(e => !e.IsUnparsed), e => Assert.NotNull(e.SourceContext));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(100_000)]
    public void Clef_in_chunks_gives_the_same_events_as_one_pass(int chunkSize)
    {
        var text = SampleText("serilog-clef", "log20261113.clef").ReplaceLineEndings("\r\n");
        var expected = ParseAll(LogFormats.Clef, text);

        var document = new LogDocument("x.clef", LogFormats.Clef);
        var parser = LogFormats.Clef.CreateParser("x.clef");
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var piece = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            document.Text.Append(piece);
            document.Apply(parser.Append(piece));
        }

        document.Apply(parser.Complete());

        Assert.Equal(expected, document.Events);
        Assert.All(document.Events, e => Assert.Equal(document.GetRaw(e), text.Substring((int)e.Offset, e.Length)));
    }

    [Fact]
    public void Clef_partial_line_is_held_back_and_can_be_previewed()
    {
        var parser = LogFormats.Clef.CreateParser(null);
        var first = parser.Append("{\"@t\":\"2026-11-13T04:45:32Z\",\"@mt\":\"one\"}\n{\"@t\":\"2026-11-13T04:45:33Z\",\"@mt\":\"tw");

        Assert.Equal("one", Assert.Single(first.Events).Message);
        Assert.True(parser.HasPartialLine);
        Assert.Equal(1, parser.CommittedId);

        var preview = parser.Preview();
        Assert.Equal(1, preview.FirstId);
        Assert.True(Assert.Single(preview.Events).IsUnparsed); // half a JSON object is text for now
        Assert.Equal(1, parser.CommittedId);

        var second = parser.Append("o\"}\n");
        Assert.Equal(1, second.FirstId);
        Assert.Equal("two", Assert.Single(second.Events).Message);
        Assert.False(parser.HasPartialLine);
        Assert.Empty(parser.Complete().Events);
    }

    [Fact]
    public void Clef_parser_continues_a_document_at_an_offset_and_id()
    {
        var parser = LogFormats.Clef.CreateParser("b.clef", startOffset: 1000, firstId: 7);

        var delta = parser.Append("{\"@t\":\"2026-11-13T04:45:32Z\",\"@mt\":\"x\"}\n");

        var e = Assert.Single(delta.Events);
        Assert.Equal(7, delta.FirstId);
        Assert.Equal(7, e.Id);
        Assert.Equal(1000, e.Offset);
        Assert.Equal("b.clef", e.SourceFile);
    }

    [Fact]
    public void Formats_recognise_their_own_lines_only()
    {
        const string clef = """{"@t":"2026-11-13T04:45:32Z","@mt":"x"}""";
        const string json = """{"Timestamp":"2026-11-13T10:15:32+05:30","Level":"Information","MessageTemplate":"x"}""";
        const string text = "2026-11-13 08:00:00.012 +05:30 [INF] hello";

        Assert.True(LogFormats.Clef.Recognises(clef));
        Assert.False(LogFormats.Clef.Recognises(json));
        Assert.False(LogFormats.Clef.Recognises(text));
        Assert.True(LogFormats.SerilogJson.Recognises(json));
        Assert.False(LogFormats.SerilogJson.Recognises(clef));
        Assert.True(LogFormats.SerilogText.Recognises(text));
        Assert.False(LogFormats.SerilogText.Recognises(clef));
        Assert.False(LogFormats.Clef.Recognises("{ broken"));
    }

    // ------------------------------------------------------------------ classic JSON

    [Fact]
    public void Serilog_json_sample_file_parses_as_documented()
    {
        var events = ParseAll(LogFormats.SerilogJson, SampleText("serilog-json", "log20261113.json"), "x.json");

        Assert.Equal(7, events.Count);
        Assert.DoesNotContain(events, e => e.IsUnparsed);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 0, 0, 12, TimeSpan.FromHours(5.5)), events[0].Timestamp);
        Assert.Equal("Loaded configuration from \"appsettings.json\"", events[1].Message);
        Assert.Equal("HTTP \"GET\" \"/api/orders/32131\" responded 200 in 810.4123 ms", events[2].Message); // from Renderings
        Assert.Equal("Retry 2/3 for payment gateway", events[3].Message); // RenderedMessage
        Assert.Equal(LogLevel.Error, events[4].Level);
        Assert.StartsWith("System.InvalidOperationException", events[4].Exception);
        Assert.Equal("Basket Basket { Id: \"b-9917\", Items: [\"SKU-1\", \"SKU-7\"], Total: 42.5 } checked out", events[5].Message);
        Assert.Equal("Shop.Worker", events[6].SourceContext);
        Assert.Equal("Unhandled exception in {Worker}", events[6].MessageTemplate);
    }

    // ------------------------------------------------------------------ message templates

    [Theory]
    [InlineData("Hello {Name}", "Hello \"Ann\"")]
    [InlineData("Hello {Name:l}", "Hello Ann")]
    [InlineData("Took {Ms:0.0} ms", "Took 12.3 ms")]
    [InlineData("[{Count,5}] [{Count,-5}]", "[    7] [7    ]")]
    [InlineData("{Missing} stays", "{Missing} stays")]
    [InlineData("{{literal}} and {Name}", "{literal} and \"Ann\"")]
    [InlineData("Ok: {Ok}", "Ok: True")]
    [InlineData("Nothing: {Nothing}", "Nothing: null")]
    [InlineData("Seq: {Seq}", "Seq: [1, \"two\"]")]
    [InlineData("Json: {Seq:j}", "Json: [1, \"two\"]")]
    [InlineData("Positional {0}", "Positional \"zero\"")]
    [InlineData("Unclosed {Name", "Unclosed {Name")]
    [InlineData("Bad format {Ms:Q}", "Bad format 12.34")]
    public void Templates_render_like_serilog(string template, string expected)
    {
        var properties = new Dictionary<string, LogValue>
        {
            ["Name"] = new("Ann"),
            ["Ms"] = new(12.34),
            ["Count"] = new(7L),
            ["Ok"] = new(true),
            ["Nothing"] = LogValue.Null,
            ["Seq"] = new(new List<LogValue> { new(1L), new("two") }),
            ["0"] = new("zero"),
        };

        Assert.Equal(expected, MessageTemplateRenderer.Render(template, properties));
    }

    [Fact]
    public void Renderings_replace_formatted_holes_in_order()
    {
        var properties = new Dictionary<string, LogValue> { ["A"] = new(1.0), ["B"] = new("x"), ["C"] = new(2.0) };

        var text = MessageTemplateRenderer.Render("{A:0.00} {B} {C:0.00}", properties, ["1,00", "2,00"]);

        Assert.Equal("1,00 \"x\" 2,00", text);
    }

    // ------------------------------------------------------------------ values

    [Fact]
    public void Json_values_keep_their_shape()
    {
        using var json = JsonDocument.Parse("""{"s":"t","i":5,"d":1.5,"e":1e3,"b":false,"n":null,"a":[1,"x"],"o":{"k":{"z":1}}}""");
        var root = json.RootElement;

        Assert.Equal("t", LogValue.FromJson(root.GetProperty("s")).Value);
        Assert.Equal(5L, LogValue.FromJson(root.GetProperty("i")).Value);
        Assert.Equal(1.5m, LogValue.FromJson(root.GetProperty("d")).Value);
        Assert.Equal(1000d, LogValue.FromJson(root.GetProperty("e")).Value);
        Assert.Equal(false, LogValue.FromJson(root.GetProperty("b")).Value);
        Assert.True(LogValue.FromJson(root.GetProperty("n")).IsNull);
        Assert.Equal("[1, \"x\"]", LogValue.FromJson(root.GetProperty("a")).ToString());
        Assert.Equal("{ \"k\": { \"z\": 1 } }", LogValue.FromJson(root.GetProperty("o")).ToString());
        Assert.Equal("{\"z\":1}", LogValue.FromJson(root.GetProperty("o"), maxDepth: 1).AsStructure!["k"].Value); // kept as its JSON text
    }

    [Fact]
    public void Values_compare_by_content()
    {
        var a = new LogValue(new Dictionary<string, LogValue> { ["x"] = new(new List<LogValue> { new(1L) }) });
        var b = new LogValue(new Dictionary<string, LogValue> { ["x"] = new(new List<LogValue> { new(1L) }) });

        Assert.Equal(a, b);
        Assert.NotEqual(a, new LogValue("x"));
        Assert.Equal(new LogValue(3), new LogValue(3L));
        Assert.Equal("text \"quoted\"\n", new LogValue("text \"quoted\"\n").ToString());
        Assert.Equal("\"text \\\"quoted\\\"\\n\"", new LogValue("text \"quoted\"\n").ToJson());
    }

    [Fact]
    public void Events_with_equal_properties_are_equal()
    {
        var first = new LogEvent(0, null, 0, 1, DateTimeOffset.UnixEpoch, LogLevel.Information, "m", null, "t", new Dictionary<string, LogValue> { ["A"] = new(1L) });
        var second = first with { Properties = new Dictionary<string, LogValue> { ["A"] = new(1L) } };

        Assert.Equal(first, second);
        Assert.NotEqual(first, second with { Properties = new Dictionary<string, LogValue> { ["A"] = new(2L) } });
        Assert.NotEqual(first, second with { MessageTemplate = null });
    }
}
