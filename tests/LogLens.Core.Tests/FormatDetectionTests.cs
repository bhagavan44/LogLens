using LogLens.Core.Formats;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.Core.Tests;

public class FormatDetectionTests
{
    private const string CustomTemplateText = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({ThreadId}) {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private static string Sample(string folder, string file)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", folder, file));

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static FormatCatalog WithCustom() => new([new CustomTemplate("Shop", CustomTemplateText)]);

    [Fact]
    public void Each_sample_is_detected_as_its_own_format()
    {
        var catalog = WithCustom();

        Assert.Same(LogFormats.Clef, catalog.Detect(Lines(Sample("serilog-clef", "log20261113.clef"))));
        Assert.Same(LogFormats.SerilogJson, catalog.Detect(Lines(Sample("serilog-json", "log20261113.json"))));
        Assert.Same(LogFormats.SerilogText, catalog.Detect(Lines(Sample("serilog-text", "log20261113.txt"))));
        Assert.Equal("template:Shop", catalog.Detect(Lines(Sample("serilog-text", "custom-template-20261113.txt"))).Id);
    }

    [Fact]
    public void Without_the_custom_template_its_lines_fall_back_to_the_default_template()
    {
        // The default template matches any "timestamp [LVL] text" line, so the file still reads (the context ends up in the message).
        Assert.Same(LogFormats.SerilogText, FormatCatalog.BuiltIn.Detect(Lines(Sample("serilog-text", "custom-template-20261113.txt"))));
    }

    [Fact]
    public void Nothing_recognised_gives_the_default_format()
    {
        Assert.Same(LogFormats.Default, FormatCatalog.BuiltIn.Detect(["hello", "world"]));
        Assert.Same(LogFormats.Default, FormatCatalog.BuiltIn.Detect([]));
    }

    [Fact]
    public void The_format_recognising_most_lines_wins()
    {
        string[] lines =
        [
            "2026-11-13 08:00:00.012 +05:30 [INF] text one",
            """{"@t":"2026-11-13T04:45:32Z","@mt":"a"}""",
            """{"@t":"2026-11-13T04:45:33Z","@mt":"b"}""",
        ];

        Assert.Same(LogFormats.Clef, FormatCatalog.BuiltIn.Detect(lines));
    }

    [Fact]
    public void Catalog_lists_custom_templates_between_json_and_the_default()
    {
        var catalog = new FormatCatalog(
        [
            new CustomTemplate("Shop", CustomTemplateText),
            new CustomTemplate("Broken", "{Timestamp} {Message}"), // no level: left out
            new CustomTemplate("shop", CustomTemplateText),         // name taken: left out
            new CustomTemplate(" ", CustomTemplateText),            // no name: left out
        ]);

        Assert.Equal(["clef", "serilog-json", "template:Shop", "serilog-text"], catalog.All.Select(f => f.Id));
        var custom = Assert.Single(catalog.Custom);
        Assert.Equal("Shop", custom.DisplayName);
        Assert.Same(custom, catalog.Find("template:Shop"));
        Assert.Null(catalog.Find("template:Gone"));
        Assert.Null(catalog.Find(null));
        Assert.Equal(LogFields.Exception | LogFields.Properties | LogFields.SourceContext, custom.Fields);
    }

    [Fact]
    public void Template_validation_explains_the_problem()
    {
        Assert.True(FormatCatalog.TryCompile(CustomTemplateText, out var compiled, out _));
        Assert.Equal(["ThreadId", "SourceContext"], compiled!.PropertyNames);

        Assert.False(FormatCatalog.TryCompile("{Timestamp} {Message}", out _, out var error));
        Assert.Contains("{Level}", error);
        Assert.False(FormatCatalog.TryCompile("", out _, out error));
        Assert.Contains("Enter", error);
    }

    // ------------------------------------------------------------------ custom template parsing

    [Fact]
    public void Custom_template_properties_become_event_properties()
    {
        var format = Assert.Single(WithCustom().Custom);
        var text = Sample("serilog-text", "custom-template-20261113.txt");
        var events = Parse(format.CreateParser("c.txt"), text, text.Length);

        Assert.Equal(5, events.Count);
        Assert.Equal("Application starting up", events[0].Message);
        Assert.Equal(1L, events[0].Property("ThreadId")!.Value);
        Assert.Equal("Shop.Program", events[0].SourceContext);
        Assert.Equal("Payment failed for order 67204", events[3].Message);
        Assert.StartsWith("System.InvalidOperationException", events[3].Exception);
        Assert.Equal(12L, events[3].Property("ThreadId")!.Value);
    }

    [Fact]
    public void Quoted_template_values_lose_their_quotes()
    {
        var format = new SerilogTextFormat(OutputTemplate.Parse("{Timestamp:HH:mm:ss} {Level:u3} {User} {Message}"), "Users");
        var e = Assert.Single(Parse(format.CreateParser(null), "08:00:05 INF \"Ann \\\"A\\\" Lee\" signed in\n", 100));

        Assert.Equal("Ann \"A\" Lee", e.Property("User")!.Value);
        Assert.Equal("signed in", e.Message);
    }

    // ------------------------------------------------------------------ detecting parser

    [Theory]
    [InlineData("serilog-clef", "log20261113.clef", 1)]
    [InlineData("serilog-clef", "log20261113.clef", 50)]
    [InlineData("serilog-json", "log20261113.json", 7)]
    [InlineData("serilog-text", "log20261113.txt", 13)]
    [InlineData("serilog-text", "log20261113.txt", 1_000_000)]
    public void Detecting_parser_gives_the_same_events_as_the_detected_format(string folder, string file, int chunkSize)
    {
        var text = Sample(folder, file);
        var expectedFormat = FormatCatalog.BuiltIn.Detect(Lines(text));
        var expected = Parse(expectedFormat.CreateParser("f"), text, text.Length);

        var detecting = new DetectingParser(FormatCatalog.BuiltIn, "f");
        var actual = Parse(detecting, text, chunkSize);

        Assert.Same(expectedFormat, detecting.Detected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Nothing_is_listed_or_detected_until_a_complete_line_arrives()
    {
        var parser = new DetectingParser(FormatCatalog.BuiltIn);

        var delta = parser.Append("{\"@t\":\"2026-11-13T04:45:32Z\",");

        Assert.Empty(delta.Events);
        Assert.Null(parser.Detected);
        Assert.True(parser.HasPartialLine);
        Assert.Equal(29, parser.EndOffset);

        delta = parser.Append("\"@mt\":\"x\"}\n");

        Assert.Same(LogFormats.Clef, parser.Detected);
        Assert.Equal("x", Assert.Single(delta.Events).Message);
        Assert.Equal(1, parser.CommittedId);
    }

    [Fact]
    public void A_file_without_a_final_newline_is_detected_on_preview_and_complete()
    {
        var parser = new DetectingParser(FormatCatalog.BuiltIn);
        parser.Append("{\"@t\":\"2026-11-13T04:45:32Z\",\"@mt\":\"only\"}");

        var preview = parser.Preview();

        Assert.Same(LogFormats.Clef, parser.Detected);
        Assert.Equal("only", Assert.Single(preview.Events).Message);
        Assert.Equal(0, parser.CommittedId);

        var complete = parser.Complete();
        Assert.Equal("only", Assert.Single(complete.Events).Message);
        Assert.Equal(1, parser.CommittedId);
    }

    [Fact]
    public void An_empty_stream_completes_with_the_default_format()
    {
        var parser = new DetectingParser(FormatCatalog.BuiltIn, startOffset: 10, firstId: 3);

        var delta = parser.Complete();

        Assert.Empty(delta.Events);
        Assert.Equal(3, delta.FirstId);
        Assert.Same(LogFormats.Default, parser.Detected);
    }

    [Fact]
    public void Auto_detect_format_creates_detecting_parsers()
    {
        var format = new AutoDetectFormat(FormatCatalog.BuiltIn);

        Assert.Equal("auto", format.Id);
        Assert.IsType<DetectingParser>(format.CreateParser(null));
        Assert.False(format.Recognises("anything"));
        Assert.Same(FormatCatalog.BuiltIn, ((AutoDetectFormat)LogFormats.Detect).Catalog);
    }

    [Fact]
    public void Combined_formats_name_all_parts_and_offer_every_field()
    {
        Assert.Same(LogFormats.Clef, LogFormats.Combine([LogFormats.Clef, LogFormats.Clef]));
        Assert.Same(LogFormats.Default, LogFormats.Combine([]));

        var combined = LogFormats.Combine([LogFormats.SerilogText, LogFormats.Clef]);
        Assert.Equal("Serilog text + Serilog CLEF", combined.DisplayName);
        Assert.Equal(LogFormats.Clef.Fields, combined.Fields);
        Assert.Throws<NotSupportedException>(() => combined.CreateParser(null));
    }

    [Fact]
    public void Settings_keep_valid_custom_templates_and_build_the_catalog()
    {
        var settings = new AppSettings
        {
            CustomTemplates =
            [
                new CustomTemplate(" Shop ", " " + CustomTemplateText + " "),
                new CustomTemplate("shop", "{Timestamp} {Level} {Message}"),
                new CustomTemplate("", "{Timestamp} {Level} {Message}"),
                new CustomTemplate("Empty", " "),
            ],
        }.Normalised();

        var kept = Assert.Single(settings.CustomTemplates);
        Assert.Equal(new CustomTemplate("Shop", CustomTemplateText), kept);
        Assert.Equal("template:Shop", Assert.Single(settings.ToFormatCatalog().Custom).Id);
        Assert.Same(FormatCatalog.BuiltIn, new AppSettings().ToFormatCatalog());
    }

    private static List<LogEvent> Parse(ILogParser parser, string text, int chunkSize)
    {
        var document = new LogDocument("f", LogFormats.Default);
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var piece = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            document.Text.Append(piece);
            document.Apply(parser.Append(piece));
        }

        document.Apply(parser.Complete());
        return [.. document.Events];
    }
}

public class CustomTemplateEditingTests
{
    private const string Template = "{Timestamp:HH:mm:ss} {Level:u3} ({ThreadId}) {Message}";

    [Theory]
    [InlineData("", Template, "Give the template a name")]
    [InlineData("Shop", "{Timestamp} {Message}", "{Level}")]
    [InlineData("other", Template, "already a format called 'other'")]
    [InlineData("serilog clef", Template, "already a format")]
    public void Problems_are_explained(string name, string template, string expected)
    {
        Assert.Contains(expected, FormatCatalog.CheckCustomTemplate(name, template, ["Other"]));
    }

    [Fact]
    public void A_good_template_has_no_problem()
    {
        Assert.Null(FormatCatalog.CheckCustomTemplate("Shop", Template, ["Other"]));
    }

    [Fact]
    public void Testing_a_line_shows_what_the_template_reads()
    {
        var text = FormatCatalog.TestLine(Template, "08:00:05 WRN (12) Retry 2/3\r\n");

        Assert.Contains("level Warning", text);
        Assert.Contains("message \"Retry 2/3\"", text);
        Assert.Contains("ThreadId = 12", text);
        Assert.Equal("The line doesn't match the template.", FormatCatalog.TestLine(Template, "nothing like it"));
        Assert.StartsWith("Paste", FormatCatalog.TestLine(Template, " "));
        Assert.Contains("{Level}", FormatCatalog.TestLine("{Timestamp} {Message}", "x"));
    }

    [Fact]
    public void The_settings_catalog_is_rebuilt_only_when_the_templates_change()
    {
        var service = new SettingsService(new InMemorySettingsStore());
        var builtIn = service.Formats;
        Assert.Same(FormatCatalog.BuiltIn, builtIn);

        service.Update(s => s with { CustomTemplates = [new CustomTemplate("Shop", Template)] });
        var withShop = service.Formats;
        Assert.Equal("template:Shop", Assert.Single(withShop.Custom).Id);

        service.Update(s => s with { NewestFirst = true });
        Assert.Same(withShop, service.Formats);

        service.Update(s => s with { CustomTemplates = [] });
        Assert.Same(FormatCatalog.BuiltIn, service.Formats);
    }
}
