using System.Text;
using LogLens.Core.Models;
using LogLens.Infrastructure.Loading;

namespace LogLens.Infrastructure.Tests;

public sealed class LogFileLoaderTests : IDisposable
{
    private static readonly string Text = """
        2026-11-13 08:00:00.012 +05:30 [INF] Application starting up
        2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed — 日本語
        System.InvalidOperationException: boom
           at A.B()
        2026-11-13 08:00:07.512 +05:30 [INF] Application stopped

        """.ReplaceLineEndings("\n");

    private readonly string _folder = Directory.CreateTempSubdirectory("loglens-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(byte[] bytes, string name = "log.txt")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("utf-8", false, "UTF-8")]
    [InlineData("utf-8", true, "UTF-8 with BOM")]
    [InlineData("utf-16", true, "UTF-16 LE")]
    [InlineData("utf-16BE", true, "UTF-16 BE")]
    public async Task Reads_the_supported_encodings(string encodingName, bool bom, string expectedName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(Text)).ToArray();
        var path = Write(bytes);

        var document = await new LogFileLoader().LoadAsync(path);

        Assert.Equal(expectedName, document.EncodingName);
        Assert.Equal(3, document.Events.Count);
        Assert.Equal("Payment failed — 日本語", document.Events[1].Message);
        Assert.Equal(bytes.Length, document.FileSizeBytes);
        Assert.Equal(path, document.FilePath);
        Assert.Equal(path, document.Events[0].SourceFile);
        Assert.Equal("2026-11-13 08:00:00.012 +05:30 [INF] Application starting up", document.GetRaw(document.Events[0]));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(13)]
    public async Task Small_chunks_split_multibyte_characters_and_events_without_harm(int chunkBytes)
    {
        var path = Write(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Text)).ToArray());

        var whole = await new LogFileLoader().LoadAsync(path);
        var chunked = await new LogFileLoader(chunkBytes: chunkBytes).LoadAsync(path);

        Assert.Equal(whole.Events, chunked.Events);
        Assert.Equal("UTF-8 with BOM", chunked.EncodingName);
    }

    [Fact]
    public async Task Invalid_utf8_is_replaced_not_fatal()
    {
        var bytes = Encoding.UTF8.GetBytes("2026-11-13 08:00:00.012 +05:30 [INF] bad ").Concat(new byte[] { 0xC3, 0x28, 0xFF }).Concat(Encoding.UTF8.GetBytes("\n")).ToArray();

        var document = await new LogFileLoader().LoadAsync(Write(bytes));

        Assert.StartsWith("bad ", Assert.Single(document.Events).Message);
        Assert.Contains('�', document.Events[0].Message);
    }

    [Fact]
    public async Task An_empty_file_gives_an_empty_document()
    {
        var document = await new LogFileLoader().LoadAsync(Write([]));

        Assert.Empty(document.Events);
        Assert.Equal("UTF-8", document.EncodingName);
    }

    [Fact]
    public async Task A_file_held_open_for_writing_by_another_process_still_opens()
    {
        var path = Path.Combine(_folder, "live.txt");

        // How Serilog's file sink holds its file: write access, others may read.
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(Text);
        await writer.WriteAsync(bytes);
        await writer.FlushAsync();

        var document = await new LogFileLoader().LoadAsync(path);

        Assert.Equal(3, document.Events.Count);

        // ...and the loader did not keep the file locked: the writer can still write.
        await writer.WriteAsync(Encoding.UTF8.GetBytes("2026-11-13 08:00:08.000 +05:30 [INF] more\n"));
    }

    [Fact]
    public async Task The_loaded_file_is_not_locked_and_not_changed()
    {
        var path = Write(Encoding.UTF8.GetBytes(Text));
        var before = File.GetLastWriteTimeUtc(path);

        await new LogFileLoader().LoadAsync(path);

        File.Move(path, path + ".moved"); // would fail if a handle were still open
        Assert.Equal(before, File.GetLastWriteTimeUtc(path + ".moved"));
        Assert.Equal(Text, File.ReadAllText(path + ".moved"));
    }

    [Fact]
    public async Task A_missing_file_throws_FileNotFound()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => new LogFileLoader().LoadAsync(Path.Combine(_folder, "nope.txt")));

    [Fact]
    public async Task A_missing_folder_throws_DirectoryNotFound()
        => await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new LogFileLoader().LoadAsync(Path.Combine(_folder, "no", "such.txt")));

    [Fact]
    public async Task Cancellation_stops_the_load()
    {
        var path = Write(Encoding.UTF8.GetBytes(Text));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogFileLoader().LoadAsync(path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Progress_ends_at_one()
    {
        var path = Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Text, 100))));
        var reports = new List<double>();

        await new LogFileLoader(chunkBytes: 1024).LoadAsync(path, new SynchronousProgress(reports.Add));

        Assert.True(reports.Count > 2);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task The_sample_log_loads_completely()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "serilog-text", "log20261113.txt");

        var document = await new LogFileLoader().LoadAsync(path);

        Assert.Equal(14, document.Events.Count);
        Assert.Equal(0, document.UnparsedCount);
        Assert.Equal(2, document.CountOf(LogLevel.Error));
        Assert.True(document.LoadTime > TimeSpan.Zero);
    }

    private sealed class SynchronousProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
