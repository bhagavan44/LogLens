using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LogLens.Core.Formats;
using LogLens.Core.Merging;
using LogLens.Core.Models;
using LogLens.Core.State;
using LogLens.Core.Streaming;

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

    /// <summary>A main view-model over fake streams and an in-memory state file.</summary>
    public static MainViewModel Main(FakeStreams streams, MemoryStateStore? store = null)
    {
        store ??= new MemoryStateStore();
        return new MainViewModel(streams, new RecentFilesService(store), new SessionService(store), new BookmarkService(store));
    }

    /// <summary>A line in the default template for event <paramref name="n"/>.</summary>
    public static string Line(int n, string level = "INF", string? message = null)
        => $"2026-11-13 08:{n / 60 % 60:00}:{n % 60:00}.000 +05:30 [{level}] {message ?? "event " + n}\n";

    public static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => Line(i)));
}

/// <summary>
/// Runs a test body on one dedicated thread with its own synchronisation context, standing in for the UI thread: every continuation of the
/// view-models comes back to it, so batches are applied one at a time, as in the app.
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    private UiThread()
    {
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    public static async Task Run(Func<Task> body)
    {
        using var ui = new UiThread();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(finished == done.Task, "the test body did not finish within 20 s");
        await done.Task;
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>Waits for a condition that later continuations on the <see cref="UiThread"/> make true.</summary>
internal static class Eventually
{
    public static async Task True(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }
}

/// <summary>
/// A stream the test drives: batches are written to it, or it fails, or it ends. With <paramref name="parseAs"/> the text is parsed with that
/// format and every batch names it, as a detecting stream's batches do.
/// </summary>
internal sealed class FakeStream(string path, ILogFormat? parseAs = null) : ILogStream
{
    private readonly Channel<StreamBatch> _channel = Channel.CreateUnbounded<StreamBatch>();
    private ILogParser _parser = (parseAs ?? LogFormats.SerilogText).CreateParser(path);
    private long _bytes;

    public string Path { get; } = path;

    public ILogFormat Format => parseAs ?? LogFormats.SerilogText;

    public bool Follow { get; set; }

    public bool FollowRolled { get; set; } = true;

    public int Refreshes { get; private set; }

    public bool Disposed { get; private set; }

    public void Refresh() => Refreshes++;

    /// <summary>Delivers <paramref name="text"/> as the stream would: parsed, with the last event provisional.</summary>
    public FakeStream Write(string text, bool initialLoadComplete = false, long? fileLength = null)
    {
        _bytes += text.Length;
        _channel.Writer.TryWrite(new ChunkBatch(text, _parser.Append(text), _bytes, fileLength ?? _bytes, initialLoadComplete, Path, "UTF-8", Format: parseAs));
        return this;
    }

    /// <summary>The whole first read in one batch.</summary>
    public FakeStream Load(string text) => Write(text, initialLoadComplete: true);

    public void Send(StreamBatch batch) => _channel.Writer.TryWrite(batch);

    /// <summary>The file was truncated: the next writes start a new parse.</summary>
    public void Reset(string reason = "The file got shorter, so it was read again from the start.")
    {
        _parser = Format.CreateParser(Path);
        _bytes = 0;
        Send(new ResetBatch(reason));
    }

    public void Fail(Exception exception) => _channel.Writer.TryComplete(exception);

    public void End() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<StreamBatch> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var batch in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return batch;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <see cref="FakeStream"/>s: one prepared per path, or a missing file.</summary>
internal sealed class FakeStreams : ILogStreamFactory
{
    private readonly Dictionary<string, Func<ILogFormat?, FakeStream>> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requested { get; } = [];

    public List<FakeStream> Opened { get; } = [];

    /// <summary>
    /// Opening <paramref name="path"/> loads <paramref name="text"/> at once, parsed with the format asked for or else
    /// <paramref name="detected"/> (the default format when null).
    /// </summary>
    public void Returns(string path, string? text = null, ILogFormat? detected = null)
    {
        _texts[path] = text ?? TestSupport.Text;
        _streams[path] = format => new FakeStream(path, format ?? detected).Load(text ?? TestSupport.Text);
    }

    /// <summary>Opening <paramref name="path"/> fails with <paramref name="exception"/>.</summary>
    public void Throws(string path, Exception exception) => _streams[path] = _ =>
    {
        var stream = new FakeStream(path);
        stream.Fail(exception);
        return stream;
    };

    /// <summary>Opening <paramref name="path"/> gives a stream the test drives; nothing arrives until it writes.</summary>
    public FakeStream Waits(string path)
    {
        var stream = new FakeStream(path);
        _streams[path] = _ => stream;
        return stream;
    }

    /// <summary>The format asked for with each <see cref="Open"/> / <see cref="OpenMerged"/>, in order (null: detect).</summary>
    public List<ILogFormat?> RequestedFormats { get; } = [];

    public ILogStream Open(string path, ILogFormat? format = null)
    {
        Requested.Add(path);
        RequestedFormats.Add(format);
        var stream = _streams.TryGetValue(path, out var make) ? make(format) : Missing(path);
        Opened.Add(stream);
        return stream;
    }

    public List<IReadOnlyList<string>> MergeRequests { get; } = [];

    /// <summary>A merged view of files prepared with <see cref="Returns"/>; a file that was not prepared is missing.</summary>
    public ILogStream OpenMerged(IReadOnlyList<string> paths, ILogFormat? format = null)
    {
        MergeRequests.Add(paths);
        RequestedFormats.Add(format);
        var stream = new FakeStream(paths[^1]);
        Opened.Add(stream);
        if (paths.FirstOrDefault(p => !_texts.ContainsKey(p)) is { } missing)
        {
            stream.Fail(new MergeSourceException(missing, new FileNotFoundException("not found", missing)));
            return stream;
        }

        var merged = LogMerger.Merge([.. paths.Select(p => TestSupport.Document(_texts[p], p))]);
        stream.Send(new ChunkBatch(merged.Text, new ParseDelta(0, merged.Events), merged.Text.Length, merged.Text.Length, true, paths[^1], "UTF-8", merged.Files));
        stream.End();
        return stream;
    }

    private static FakeStream Missing(string path)
    {
        var stream = new FakeStream(path);
        stream.Fail(new FileNotFoundException("not found", path));
        return stream;
    }
}

/// <summary>An <see cref="IStateStore"/> in memory.</summary>
internal sealed class MemoryStateStore : IStateStore
{
    public AppState State { get; set; } = new();

    public AppState Load() => State;

    public void Save(AppState state) => State = state;
}
