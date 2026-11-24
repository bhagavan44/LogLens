using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.Streaming;
using LogLens.Infrastructure.Loading;

namespace LogLens.Infrastructure.Tests;

/// <summary>Format detection (FR-F1) and the format override through the real file streams.</summary>
public sealed class FormatLoadingTests : IDisposable
{
    private const string ClefLines = """
        {"@t":"2026-11-13T04:45:32Z","@mt":"Order {Id} shipped","Id":1,"SourceContext":"Shop"}
        {"@t":"2026-11-13T04:45:34Z","@mt":"Order {Id} shipped","Id":3,"SourceContext":"Shop"}

        """;

    private const string TextLines = "2026-11-13 04:45:33.000 +00:00 [WRN] Text in between\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("loglens-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    private static string Sample(string folder, string file) => Path.Combine(AppContext.BaseDirectory, "samples", folder, file);

    [Theory]
    [InlineData("serilog-clef", "log20261113.clef", "clef", 12)]
    [InlineData("serilog-json", "log20261113.json", "serilog-json", 7)]
    [InlineData("serilog-text", "log20261113.txt", "serilog-text", 14)]
    public async Task The_loader_detects_each_samples_format(string folder, string file, string formatId, int events)
    {
        var document = await new LogFileLoader().LoadAsync(Sample(folder, file));

        Assert.Equal(formatId, document.Format.Id);
        Assert.Equal(events, document.Events.Count);
    }

    [Fact]
    public async Task A_stream_from_the_factory_reports_the_detected_format_with_its_batches()
    {
        var path = Write("app.clef", ClefLines);
        var factory = new FileLogStreamFactory(new StreamOptions { UseWatcher = false }, () => FormatCatalog.BuiltIn);
        await using var stream = factory.Open(path);

        Assert.IsType<AutoDetectFormat>(stream.Format);
        var chunk = await FirstChunkAsync(stream);

        Assert.Same(LogFormats.Clef, chunk.Format);
        Assert.Equal(2, chunk.Delta.Events.Count);
        Assert.Equal("Order 1 shipped", chunk.Delta.Events[0].Message);
    }

    [Fact]
    public async Task A_chosen_format_is_used_as_it_is()
    {
        var path = Write("app.clef", ClefLines);
        var factory = new FileLogStreamFactory(new StreamOptions { UseWatcher = false }, () => FormatCatalog.BuiltIn);
        await using var stream = factory.Open(path, LogFormats.SerilogText);

        var chunk = await FirstChunkAsync(stream);

        Assert.Same(LogFormats.SerilogText, stream.Format);
        Assert.Same(LogFormats.SerilogText, chunk.Format);
        Assert.All(chunk.Delta.Events, e => Assert.True(e.IsUnparsed)); // JSON lines don't match the text template
    }

    [Fact]
    public async Task Custom_templates_from_the_catalog_take_part_in_detection()
    {
        var path = Write("custom.txt", "08:00:05 WRN Shop.Payments Retry\n08:00:06 INF Shop.Orders Done\n");
        var catalog = new FormatCatalog([new CustomTemplate("Short", "{Timestamp:HH:mm:ss} {Level:u3} {SourceContext} {Message}")]);

        var document = await new LogFileLoader(new AutoDetectFormat(catalog)).LoadAsync(path);

        Assert.Equal("template:Short", document.Format.Id);
        Assert.Equal(["Shop.Payments", "Shop.Orders"], document.Events.Select(e => e.SourceContext));
    }

    [Fact]
    public async Task A_merged_view_of_files_in_different_formats_names_both()
    {
        var clef = Write("a.clef", ClefLines);
        var text = Write("b.txt", TextLines);
        var factory = new FileLogStreamFactory(new StreamOptions { UseWatcher = false }, () => FormatCatalog.BuiltIn);
        await using var stream = factory.OpenMerged([clef, text]);

        var chunk = await FirstChunkAsync(stream);

        Assert.Equal("Serilog CLEF + Serilog text", chunk.Format!.DisplayName);
        Assert.True(chunk.Format.Fields.HasFlag(LogFields.Properties));
        Assert.Equal(["Order 1 shipped", "Text in between", "Order 3 shipped"], chunk.Delta.Events.Select(e => e.Message));
        Assert.Equal(LogLevel.Warning, chunk.Delta.Events[1].Level);
    }

    [Fact]
    public async Task Without_a_catalog_the_factory_keeps_the_options_format()
    {
        var path = Write("app.txt", TextLines);
        await using var stream = new FileLogStreamFactory(new StreamOptions { UseWatcher = false }).Open(path);

        var chunk = await FirstChunkAsync(stream);

        Assert.Same(LogFormats.Default, stream.Format);
        Assert.Same(LogFormats.Default, chunk.Format);
    }

    private static async Task<ChunkBatch> FirstChunkAsync(ILogStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var batch in stream.ReadAsync(timeout.Token))
        {
            if (batch is ChunkBatch { Delta.Events.Count: > 0 } chunk)
            {
                return chunk;
            }
        }

        throw new InvalidOperationException("The stream ended without events.");
    }
}
