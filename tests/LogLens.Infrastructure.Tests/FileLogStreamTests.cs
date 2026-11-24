using System.Text;
using LogLens.Core.Formats;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Models;
using LogLens.Core.Streaming;
using LogLens.Infrastructure.Loading;

namespace LogLens.Infrastructure.Tests;

/// <summary>Runs a <see cref="FileLogStream"/> in the background and applies its batches to a document, the way the app's view-model does.</summary>
internal sealed class StreamHarness : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    public StreamHarness(string path, StreamOptions? options = null)
    {
        Stream = new FileLogStream(path, options ?? Fast());
        Doc = new LogDocument(Path.GetFullPath(path), LogFormats.SerilogText);
    }

    public FileLogStream Stream { get; }

    public LogDocument Doc { get; }

    public List<StreamBatch> Batches { get; } = [];

    public Exception? Error { get; private set; }

    public bool Completed { get; private set; }

    public static StreamOptions Fast() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(40),
        QuiescenceDelay = TimeSpan.FromSeconds(30), // previews are tested explicitly
        FirstChunkBytes = 4096,
        ChunkBytes = 64 * 1024,
    };

    public StreamHarness Start()
    {
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in Stream.ReadAsync(_cts.Token))
                {
                    lock (_gate)
                    {
                        Batches.Add(batch);
                        switch (batch)
                        {
                            case ChunkBatch c:
                                Doc.Text.Append(c.Text);
                                Doc.Apply(c.Delta);
                                break;
                            case SwitchedBatch switched:
                                Doc.BeginFile(switched.FilePath);
                                break;
                            case ResetBatch:
                                Doc.Clear();
                                break;
                        }
                    }
                }

                Completed = true;
            }
            catch (OperationCanceledException)
            {
                Completed = true;
            }
            catch (Exception ex)
            {
                Error = ex;
            }
        });
        return this;
    }

    public T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    public string[] Messages() => Read(() => Doc.Events.Select(e => e.Message).ToArray());

    public async Task WaitFor(Func<bool> condition, string because, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Error is not null)
            {
                throw new Xunit.Sdk.XunitException($"Stream failed while waiting for {because}: {Error}");
            }

            if (Read(condition))
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {because}. Events: [{string.Join(" | ", Messages())}]");
    }

    public async Task WaitForError()
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Error is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.NotNull(Error);
    }

    public async Task WaitForLoaded() => await WaitFor(() => Batches.OfType<ChunkBatch>().Any(b => b.InitialLoadComplete), "initial load to finish");

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Stream.DisposeAsync();
        if (_pump is not null)
        {
            await Task.WhenAny(_pump, Task.Delay(2000));
        }

        _cts.Dispose();
    }
}

/// <summary>A single dedicated thread with a synchronisation context, standing in for the UI thread.</summary>
internal sealed class DedicatedThreadContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public DedicatedThreadContext()
    {
        using var started = new ManualResetEventSlim();
        var id = 0;
        _thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            started.Set();
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        { IsBackground = true };
        _thread.Start();
        started.Wait();
        ThreadId = id;
    }

    public int ThreadId { get; }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public Task RunAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource();
        Post(async _ =>
        {
            try
            {
                await work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>The Serilog text format, but every parse records the thread it ran on.</summary>
internal sealed class SpyFormat(System.Collections.Concurrent.ConcurrentBag<int> threads) : ILogFormat
{
    public string Id => LogFormats.SerilogText.Id;

    public string DisplayName => LogFormats.SerilogText.DisplayName;

    public LogFields Fields => LogFormats.SerilogText.Fields;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new SpyParser(LogFormats.SerilogText.CreateParser(sourceFile, startOffset, firstId), threads);

    public bool Recognises(string line) => LogFormats.SerilogText.Recognises(line);

    private sealed class SpyParser(ILogParser real, System.Collections.Concurrent.ConcurrentBag<int> threads) : ILogParser
    {
        public int CommittedId => real.CommittedId;

        public long EndOffset => real.EndOffset;

        public bool HasPartialLine => real.HasPartialLine;

        public ParseDelta Append(string text, CancellationToken cancellationToken = default)
        {
            threads.Add(Environment.CurrentManagedThreadId);
            return real.Append(text, cancellationToken);
        }

        public ParseDelta Preview() => real.Preview();

        public ParseDelta Complete() => real.Complete();
    }
}

public sealed class FileLogStreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loglens-tests", Guid.NewGuid().ToString("N"));

    public FileLogStreamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a watcher may still be releasing the folder; the temp folder is cleaned by the OS eventually
        }
    }

    private string P(string name = "log20261113.txt") => Path.Combine(_dir, name);

    private static string Line(int n, string level = "INF", string? message = null)
        => $"2026-11-13 08:{n / 60 % 60:00}:{n % 60:00}.000 +05:30 [{level}] {message ?? "event " + n}";

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => Line(i) + "\r\n"));

    /// <summary>
    /// Runs a file operation, retrying briefly if Windows refuses it because the reader happens to have the file open at that instant —
    /// which is exactly what a real log rotator has to cope with too.
    /// </summary>
    private static void Io(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static void Append(string path, string text) => Io(() =>
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        fs.Write(bytes);
        fs.Flush(true);
    });

    // ------------------------------------------------------------------ initial load

    [Fact]
    public async Task Initial_load_arrives_in_growing_chunks_and_matches_a_whole_file_parse()
    {
        var text = Lines(0, 1999) + "System.Exception: x\r\n   at A.B()\r\n" + Lines(2000, 2100);
        File.WriteAllText(P(), text, new UTF8Encoding(false));
        var whole = new PlainTextLogParser().Parse(text, P());

        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();

        var chunks = h.Read(() => h.Batches.OfType<ChunkBatch>().ToList());
        Assert.True(chunks.Count > 3, $"expected several chunks, got {chunks.Count}");
        Assert.Single(chunks, c => c.InitialLoadComplete);
        Assert.True(chunks.Select(c => c.Text.Length).Skip(1).Take(2).Any(l => l > chunks[0].Text.Length), "chunks should grow");
        Assert.Equal(whole.Events.Length, h.Read(() => h.Doc.Events.Count));
        Assert.Equal(whole.Events.Select(e => e.Message), h.Messages());
        Assert.Equal(whole.Events.Select(e => e.Exception), h.Read(() => h.Doc.Events.Select(e => e.Exception).ToArray()));
        Assert.Equal(text.Length, h.Read(() => h.Doc.Text.Length));
    }

    [Fact]
    public async Task The_first_rows_are_delivered_before_the_whole_file_has_been_read()
    {
        File.WriteAllText(P(), Lines(0, 30_000), new UTF8Encoding(false));
        var options = StreamHarness.Fast() with { FirstChunkBytes = 8192, ChunkBytes = 256 * 1024 };

        await using var h = new StreamHarness(P(), options).Start();
        await h.WaitForLoaded();

        var first = h.Read(() => h.Batches.OfType<ChunkBatch>().First());
        Assert.True(first.Delta.Events.Count is > 0 and < 500, $"first chunk had {first.Delta.Events.Count} events");
        Assert.True(first.BytesRead < first.FileLength / 10);
        Assert.Equal(30_001, h.Read(() => h.Doc.Events.Count));
    }

    [Fact]
    public async Task Multi_byte_characters_split_across_chunk_boundaries_survive()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 600; i++)
        {
            sb.Append(Line(i, message: $"naïve café ✓ emoji 😀 日本語 {i}")).Append("\r\n");
        }

        File.WriteAllText(P(), sb.ToString(), new UTF8Encoding(false));
        var options = StreamHarness.Fast() with { FirstChunkBytes = 4096, ChunkBytes = 4097 };

        await using var h = new StreamHarness(P(), options).Start();
        await h.WaitForLoaded();

        Assert.Equal(600, h.Read(() => h.Doc.Events.Count));
        Assert.All(h.Messages().Select((m, i) => (m, i)), x => Assert.Equal($"naïve café ✓ emoji 😀 日本語 {x.i}", x.m));
        Assert.Equal("UTF-8", h.Read(() => h.Batches.OfType<ChunkBatch>().First().EncodingName));
    }

    [Fact]
    public async Task A_missing_file_is_reported_as_not_found()
    {
        await using var h = new StreamHarness(P("nope.txt")).Start();

        await h.WaitForError();

        Assert.IsType<FileNotFoundException>(h.Error);
    }

    [Fact]
    public async Task A_missing_folder_is_reported_as_not_found()
    {
        await using var h = new StreamHarness(Path.Combine(_dir, "no", "such.txt")).Start();

        await h.WaitForError();

        Assert.IsType<DirectoryNotFoundException>(h.Error);
    }

    [Fact]
    public async Task An_empty_file_completes_its_load_with_no_events_and_then_follows()
    {
        File.WriteAllBytes(P(), []);
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        Assert.Equal(0, h.Read(() => h.Doc.Events.Count));

        h.Stream.Follow = true;
        Append(P(), Lines(0, 1));
        await h.WaitFor(() => Doc(h).Events.Count == 2, "events written to the empty file");
    }

    [Fact]
    public async Task Without_follow_the_stream_goes_idle_and_ignores_later_writes()
    {
        File.WriteAllText(P(), Lines(0, 4));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();

        Append(P(), Lines(5, 9));
        await Task.Delay(400);

        Assert.Equal(5, h.Read(() => h.Doc.Events.Count));
        Assert.Contains(h.Read(() => h.Batches.OfType<StatusBatch>().Select(b => b.State).ToList()), s => s == StreamState.Idle);
    }

    [Fact]
    public async Task Refresh_picks_up_what_was_appended_while_following_is_off()
    {
        File.WriteAllText(P(), Lines(0, 4));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();

        Append(P(), Lines(5, 9));
        await Task.Delay(300);
        Assert.Equal(5, h.Read(() => h.Doc.Events.Count)); // not following: nothing yet

        h.Stream.Refresh();
        await h.WaitFor(() => Doc(h).Events.Count == 10, "the appended events after Refresh");

        Append(P(), Lines(10, 11));
        await Task.Delay(300);
        Assert.Equal(10, h.Read(() => h.Doc.Events.Count)); // still a snapshot between refreshes

        h.Stream.Refresh();
        await h.WaitFor(() => Doc(h).Events.Count == 12, "the second refresh");
        Assert.False(h.Stream.Follow);
    }

    [Fact]
    public async Task Refresh_reloads_a_file_that_was_truncated_while_following_is_off()
    {
        File.WriteAllText(P(), Lines(0, 9));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();

        Io(() => File.WriteAllText(P(), Lines(100, 101))); // shorter content, same name
        h.Stream.Refresh();

        await h.WaitFor(() => h.Batches.OfType<ResetBatch>().Any(), "a reset");
        await h.WaitFor(() => Doc(h).Events.Count == 2, "the reloaded file");
        Assert.Equal(["event 100", "event 101"], h.Messages());
    }

    // ------------------------------------------------------------------ following

    [Fact]
    public async Task Following_delivers_appended_events_within_a_second()
    {
        File.WriteAllText(P(), Lines(0, 4));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Append(P(), Lines(5, 7));
        await h.WaitFor(() => Doc(h).Events.Count == 8, "three appended events", timeoutMs: 3000);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms (NFR-7: ≤ 1 s)");
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "event " + i), h.Messages());

        Append(P(), Lines(8, 9));
        await h.WaitFor(() => Doc(h).Events.Count == 10, "two more events");
    }

    private static LogDocument Doc(StreamHarness h) => h.Doc;

    [Fact]
    public async Task Turning_follow_on_later_catches_up_on_everything_written_in_the_meantime()
    {
        File.WriteAllText(P(), Lines(0, 2));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();

        Append(P(), Lines(3, 6));
        h.Stream.Follow = true;

        await h.WaitFor(() => Doc(h).Events.Count == 7, "catch-up");
    }

    [Fact]
    public async Task An_unterminated_line_is_held_back_until_its_newline_arrives()
    {
        File.WriteAllText(P(), Lines(0, 1));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        Append(P(), "2026-11-13 08:00:09.000 +05:30 [INF] half writ");
        await Task.Delay(400);
        Assert.Equal(["event 0", "event 1"], h.Messages()); // the fragment is nowhere, not even glued onto "event 1"

        Append(P(), "ten message\r\n");
        await h.WaitFor(() => Doc(h).Events.Count == 3, "the completed line");
        Assert.Equal("half written message", h.Messages()[2]);
    }

    [Fact]
    public async Task Stack_traces_written_after_the_message_extend_the_same_event()
    {
        File.WriteAllText(P(), Line(0, "ERR", "Payment failed") + "\r\n");
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;
        Assert.Null(h.Read(() => h.Doc.Events[0].Exception));

        Append(P(), "System.InvalidOperationException: boom\r\n   at A.B()\r\n");
        await h.WaitFor(() => Doc(h).Events[0].Exception is not null, "the exception to be attached");

        Assert.Equal(1, h.Read(() => h.Doc.Events.Count));
        Assert.Contains("boom", h.Read(() => h.Doc.Events[0].Exception!));

        Append(P(), Line(1) + "\r\n");
        await h.WaitFor(() => Doc(h).Events.Count == 2, "the next event");
        Assert.Contains("boom", h.Read(() => h.Doc.Events[0].Exception!)); // still attached
    }

    [Fact]
    public async Task A_last_line_without_a_newline_appears_after_the_quiet_period_and_is_replaced_when_completed()
    {
        File.WriteAllText(P(), Line(0) + "\r\n" + Line(1));
        var options = StreamHarness.Fast() with { QuiescenceDelay = TimeSpan.FromMilliseconds(250) };
        await using var h = new StreamHarness(P(), options).Start();
        await h.WaitForLoaded();

        await h.WaitFor(() => Doc(h).Events.Count == 2, "the preview of the unterminated last line", timeoutMs: 4000);
        Assert.Equal(["event 0", "event 1"], h.Messages());

        h.Stream.Follow = true;
        Append(P(), "\r\n" + Line(2) + "\r\n");
        await h.WaitFor(() => Doc(h).Events.Count == 3, "the rest");
        Assert.Equal(["event 0", "event 1", "event 2"], h.Messages());
    }

    [Fact]
    public async Task A_writer_that_keeps_the_file_open_does_not_block_reading()
    {
        await using var writer = new FileStream(P(), FileMode.Create, FileAccess.Write, FileShare.Read);
        async Task Write(string s)
        {
            await writer.WriteAsync(new UTF8Encoding(false).GetBytes(s));
            await writer.FlushAsync();
        }

        await Write(Lines(0, 2));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        await Write(Lines(3, 5));

        await h.WaitFor(() => Doc(h).Events.Count == 6, "events from the open writer");
    }

    // ------------------------------------------------------------------ truncation / replacement

    [Fact]
    public async Task A_truncated_file_is_reloaded_from_the_start()
    {
        File.WriteAllText(P(), Lines(0, 50));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        Io(() => File.WriteAllText(P(), Lines(100, 102))); // shorter content, same name
        await h.WaitFor(() => h.Batches.OfType<ResetBatch>().Any(), "a reset");
        await h.WaitFor(() => Doc(h).Events.Count == 3, "the new content");

        Assert.Equal(["event 100", "event 101", "event 102"], h.Messages());
        Assert.Contains("shorter", h.Read(() => h.Batches.OfType<ResetBatch>().First().Reason));
    }

    [Fact]
    public async Task A_replaced_file_of_the_same_or_greater_size_is_detected_by_its_first_bytes()
    {
        File.WriteAllText(P(), Lines(0, 10));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        // Swap the file in atomically (no moment where it is empty), as a log rotator would.
        File.WriteAllText(P("swap.tmp"), Lines(500, 530));
        Io(() => File.Move(P("swap.tmp"), P(), overwrite: true));
        await h.WaitFor(() => h.Batches.OfType<ResetBatch>().Any(), "a reset");
        await h.WaitFor(() => Doc(h).Events.Count == 31, "the replacement content");

        Assert.Equal("event 500", h.Messages()[0]);
        Assert.Contains("replaced", h.Read(() => h.Batches.OfType<ResetBatch>().First().Reason));
    }

    [Fact]
    public async Task A_file_that_disappears_is_waited_for_and_reloaded_when_it_returns()
    {
        File.WriteAllText(P(), Lines(0, 5));
        await using var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        Io(() => File.Delete(P()));
        await h.WaitFor(() => h.Batches.OfType<StatusBatch>().Any(b => b.State == StreamState.WaitingForFile), "a waiting status");

        Io(() => File.WriteAllText(P(), Lines(200, 203)));
        await h.WaitFor(() => Doc(h).Events.Count == 4 && Doc(h).Events[0].Message == "event 200", "the recreated file");
    }

    // ------------------------------------------------------------------ rolling logs

    [Fact]
    public async Task A_rolling_log_is_followed_to_its_next_file()
    {
        File.WriteAllText(P("log20261113.txt"), Lines(0, 2));
        await using var h = new StreamHarness(P("log20261113.txt")).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;
        Append(P("log20261113.txt"), Lines(3, 4));
        await h.WaitFor(() => Doc(h).Events.Count == 5, "the rest of day one");

        File.WriteAllText(P("log20261114.txt"), Lines(100, 102));

        await h.WaitFor(() => h.Batches.OfType<SwitchedBatch>().Any(), "the switch", timeoutMs: 6000);
        await h.WaitFor(() => Doc(h).Events.Count == 8, "day two's events");

        Assert.Equal(P("log20261114.txt"), h.Read(() => h.Batches.OfType<SwitchedBatch>().Single().FilePath));
        Assert.Equal(["event 0", "event 1", "event 2", "event 3", "event 4", "event 100", "event 101", "event 102"], h.Messages());
        Assert.Equal(Enumerable.Range(0, 8), h.Read(() => h.Doc.Events.Select(e => e.Id).ToArray()));
        Assert.EndsWith("log20261113.txt", h.Read(() => h.Doc.Events[0].SourceFile)!);
        Assert.EndsWith("log20261114.txt", h.Read(() => h.Doc.Events[7].SourceFile)!);
        Assert.Equal("event 101", h.Read(() => h.Doc.GetRaw(h.Doc.Events[6])).Split(' ').Last() == "101" ? "event 101" : "wrong raw text");
        Assert.Equal(P("log20261114.txt"), h.Read(() => h.Doc.CurrentFilePath));
        Assert.Equal(2, h.Read(() => h.Doc.LineNumberOf(h.Doc.Events[6]))); // counted within day two's file

        Append(P("log20261114.txt"), Lines(103, 103));
        await h.WaitFor(() => Doc(h).Events.Count == 9, "events written to the new file");
    }

    [Fact]
    public async Task The_roll_does_not_lose_lines_written_to_the_old_file_just_before_it()
    {
        File.WriteAllText(P("log20261113.txt"), Lines(0, 1));
        await using var h = new StreamHarness(P("log20261113.txt")).Start();
        await h.WaitForLoaded();

        Append(P("log20261113.txt"), Lines(2, 3));
        File.WriteAllText(P("log20261114.txt"), Lines(50, 50));
        h.Stream.Follow = true;

        await h.WaitFor(() => Doc(h).Events.Count == 5, "all events from both files", timeoutMs: 6000);
        Assert.Equal(["event 0", "event 1", "event 2", "event 3", "event 50"], h.Messages());
    }

    [Fact]
    public async Task Rolling_can_be_switched_off()
    {
        File.WriteAllText(P("log20261113.txt"), Lines(0, 1));
        await using var h = new StreamHarness(P("log20261113.txt")).Start();
        await h.WaitForLoaded();
        h.Stream.FollowRolled = false;
        h.Stream.Follow = true;

        File.WriteAllText(P("log20261114.txt"), Lines(100, 101));
        await Task.Delay(500);

        Assert.Empty(h.Read(() => h.Batches.OfType<SwitchedBatch>().ToList()));
        Assert.Equal(2, h.Read(() => h.Doc.Events.Count));
    }

    [Fact]
    public async Task A_log_without_digits_in_its_name_never_rolls()
    {
        File.WriteAllText(P("app.log"), Lines(0, 1));
        File.WriteAllText(P("app2.log"), Lines(9, 9));
        await using var h = new StreamHarness(P("app.log")).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;
        await Task.Delay(400);

        Assert.Empty(h.Read(() => h.Batches.OfType<SwitchedBatch>().ToList()));
    }

    // ------------------------------------------------------------------ lifetime

    [Fact]
    public async Task Reading_and_parsing_never_run_on_the_consumers_thread()
    {
        // Regression: an async iterator resumes on its caller's thread after each yield, so doing the work in the iterator ran every
        // chunk after the first on the UI thread. Here the consumer is a single "UI" thread with its own synchronisation context.
        File.WriteAllText(P(), Lines(0, 7_999), new UTF8Encoding(false));
        var parseThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        var options = StreamHarness.Fast() with { Format = new SpyFormat(parseThreads), FirstChunkBytes = 4096, ChunkBytes = 16 * 1024 };

        using var ui = new DedicatedThreadContext();
        var chunks = 0;
        await using var stream = new FileLogStream(P(), options);
        await ui.RunAsync(async () =>
        {
            await foreach (var batch in stream.ReadAsync(CancellationToken.None))
            {
                Assert.Equal(ui.ThreadId, Environment.CurrentManagedThreadId); // batches are delivered on the consumer's thread
                if (batch is ChunkBatch c)
                {
                    chunks++;
                    if (c.InitialLoadComplete)
                    {
                        break;
                    }
                }
            }
        });

        Assert.True(chunks > 5, $"expected several chunks, got {chunks}");
        Assert.NotEmpty(parseThreads);
        Assert.DoesNotContain(ui.ThreadId, parseThreads); // but the reading and parsing were not done there
    }

    [Fact]
    public async Task Cancelling_ends_the_stream_promptly()
    {
        File.WriteAllText(P(), Lines(0, 2));
        var h = new StreamHarness(P()).Start();
        await h.WaitForLoaded();
        h.Stream.Follow = true;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await h.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Null(h.Error);
        Assert.True(h.Completed);
    }

    [Fact]
    public async Task Following_never_modifies_or_locks_the_file()
    {
        File.WriteAllText(P(), Lines(0, 5));
        var before = File.ReadAllBytes(P());
        var stamp = File.GetLastWriteTimeUtc(P());
        await using (var h = new StreamHarness(P()).Start())
        {
            await h.WaitForLoaded();
            h.Stream.Follow = true;
            await Task.Delay(200);
        }

        Assert.Equal(before, File.ReadAllBytes(P()));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P()));
        File.Delete(P()); // would throw if still locked
    }
}
