using LogLens.Core.Formats;
using LogLens.Core.Loading;
using LogLens.Core.Models;

namespace LogLens.ViewModels.Tests;

internal static class TestSupport
{
    public static readonly string Text = """
        2026-11-13 08:00:00.012 +05:30 [INF] Application starting up
        2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed
        System.InvalidOperationException: boom
           at A.B()
        2026-11-13 08:00:06.177 +05:30 [WRN] Message with an embedded newline:
        second line
        """.ReplaceLineEndings("\n");

    public static LogDocument Document(string? text = null, string path = @"C:\logs\app.txt")
    {
        text ??= Text;
        var document = new LogDocument(path, LogFormats.SerilogText) { EncodingName = "UTF-8", FileSizeBytes = 2048 };
        var parser = LogFormats.SerilogText.CreateParser(path);
        document.Text.Append(text);
        document.Apply(parser.Append(text));
        document.Apply(parser.Complete());
        return document;
    }
}

/// <summary>A loader whose result the test decides: a document per path, an exception, or a load that waits until released.</summary>
internal sealed class FakeLoader : ILogDocumentLoader
{
    private readonly Dictionary<string, Func<CancellationToken, Task<LogDocument>>> _results = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requested { get; } = [];

    public void Returns(string path, LogDocument document) => _results[path] = _ => Task.FromResult(document);

    public void Throws(string path, Exception exception) => _results[path] = _ => Task.FromException<LogDocument>(exception);

    /// <summary>The load of <paramref name="path"/> waits for the returned source (or for cancellation).</summary>
    public TaskCompletionSource<LogDocument> Waits(string path)
    {
        var source = new TaskCompletionSource<LogDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _results[path] = token =>
        {
            token.Register(() => source.TrySetCanceled(token));
            return source.Task;
        };
        return source;
    }

    public Task<LogDocument> LoadAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Requested.Add(path);
        return _results.TryGetValue(path, out var result) ? result(cancellationToken) : Task.FromException<LogDocument>(new FileNotFoundException(path));
    }
}
