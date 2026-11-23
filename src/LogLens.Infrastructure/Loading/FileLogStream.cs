using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using LogLens.Core.Formats;
using LogLens.Core.Streaming;

namespace LogLens.Infrastructure.Loading;

/// <summary>Options for <see cref="FileLogStream"/>.</summary>
public sealed record StreamOptions
{
    /// <summary>The format the file is parsed with.</summary>
    public ILogFormat Format { get; init; } = LogFormats.Default;

    /// <summary>Largest read at once. Reads start small, so the first rows appear quickly, and grow to this (NFR-2).</summary>
    public int ChunkBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>First read size; each following read is four times larger, up to <see cref="ChunkBytes"/>.</summary>
    public int FirstChunkBytes { get; init; } = 256 * 1024;

    /// <summary>How often to look for new data while following. The file-system watcher usually reacts sooner; polling covers shares (FR-T1).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>When a file ends mid-line and stays that way this long, the unfinished line is shown anyway (a file may lack a final newline).</summary>
    public TimeSpan QuiescenceDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Use a <see cref="FileSystemWatcher"/> to notice changes sooner than the next poll.</summary>
    public bool UseWatcher { get; init; } = true;

    /// <summary>Batch mode (<see cref="LogFileLoader"/>): once the existing content is read, make everything final and end the stream.</summary>
    public bool CompleteAtEndOfInitialLoad { get; init; }
}

/// <summary>Opens <see cref="FileLogStream"/>s on local files and Windows shares.</summary>
public sealed class FileLogStreamFactory(StreamOptions? options = null) : ILogStreamFactory
{
    public ILogStream Open(string path) => new FileLogStream(path, options);

    public ILogStream OpenMerged(IReadOnlyList<string> paths) => new MergedLogStream(paths, options?.Format);
}

/// <summary>
/// Reads a log file in chunks and, while <see cref="Follow"/> is on, keeps following it (SPEC §6.3). The file is opened with
/// <c>FileShare.ReadWrite | FileShare.Delete</c> for each read and closed again, so the app writing it — or a log rotator — is never blocked
/// (FR-O5); the viewer only reads.
/// </summary>
/// <remarks>
/// Reads the existing content in growing chunks; then, while following, wakes on file-system events or every poll interval and delivers what
/// was appended (FR-T1). A shorter file, or one whose first bytes changed, was truncated or replaced: reported with a <see cref="ResetBatch"/>
/// and read again (FR-T3). With <see cref="FollowRolled"/> it moves on to the next file of a rolling log (<see cref="RollPattern"/>) once the
/// current one is drained. An unfinished last line is held back until its newline arrives (FR-T6), or shown once the file has been quiet
/// for <see cref="StreamOptions.QuiescenceDelay"/>. All work runs on the thread pool.
/// </remarks>
public sealed class FileLogStream : ILogStream
{
    private const int HeadSignatureBytes = 256;

    private readonly string _path;
    private readonly StreamOptions _options;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _cts;
    private volatile bool _follow;
    private volatile bool _followRolled = true;
    private volatile bool _disposed;

    public FileLogStream(string path, StreamOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _options = options ?? new StreamOptions();
    }

    public ILogFormat Format => _options.Format;

    public bool Follow
    {
        get => _follow;
        set
        {
            _follow = value;
            Signal();
        }
    }

    public bool FollowRolled
    {
        get => _followRolled;
        set
        {
            _followRolled = value;
            Signal();
        }
    }

    public void Refresh() => Signal();

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the read loop already finished
        }

        Signal();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Batches, in order. Reading and parsing run on a producer task on the thread pool; this method only hands finished batches over through a
    /// small bounded channel. (An async iterator resumes on its <em>caller's</em> thread after each <c>yield</c>, so doing the work here would run
    /// it on the UI thread.) The bound limits how far reading runs ahead of the consumer.
    /// </summary>
    public async IAsyncEnumerable<StreamBatch> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = linked;
        if (_disposed)
        {
            yield break;
        }

        var channel = Channel.CreateBounded<StreamBatch>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() => ProduceAsync(channel.Writer, linked.Token), CancellationToken.None);
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return batch;
            }
        }
        finally
        {
            linked.Cancel(); // the consumer stopped (or the stream ended): stop producing
            await Task.WhenAny(producer, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _cts = null;
        }
    }

    private async Task ProduceAsync(ChannelWriter<StreamBatch> output, CancellationToken cancellationToken)
    {
        try
        {
            await ProduceCoreAsync(output, cancellationToken).ConfigureAwait(false);
            output.TryComplete();
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
        }
        catch (Exception ex)
        {
            output.TryComplete(ex); // reaches the consumer: file missing, access denied …
        }
    }

    private async Task ProduceCoreAsync(ChannelWriter<StreamBatch> output, CancellationToken cancellationToken)
    {
        async ValueTask Emit(StreamBatch batch) => await output.WriteAsync(batch, cancellationToken).ConfigureAwait(false);

        using var watcher = _options.UseWatcher && !_options.CompleteAtEndOfInitialLoad ? CreateWatcher() : null;
        var reader = new Reader(_path, _options, parser: null);
        var everExisted = false;
        var waitingReported = false;
        var previewed = false;
        DateTime? previewDue = null;

        await Emit(new StatusBatch(StreamState.Loading));

        while (!_disposed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = reader.Step(cancellationToken);

            switch (step.Kind)
            {
                case StepKind.Missing:
                    if (!everExisted && !reader.InitialDone)
                    {
                        throw step.Error!; // the file to open is not there: report it the way File.Open would
                    }

                    if (!waitingReported)
                    {
                        waitingReported = true;
                        await Emit(new StatusBatch(StreamState.WaitingForFile, $"Waiting for {Path.GetFileName(reader.Path)}…"));
                    }

                    break;

                case StepKind.Reset:
                    everExisted = true;
                    waitingReported = false;
                    await Emit(new ResetBatch(step.Reason!));
                    reader = new Reader(reader.Path, _options, parser: null);
                    previewed = false;
                    previewDue = null;
                    await Emit(new StatusBatch(StreamState.Loading));
                    continue;

                case StepKind.Data:
                    everExisted = true;
                    previewed = false;
                    previewDue = null;
                    if (waitingReported)
                    {
                        waitingReported = false;
                        await Emit(new StatusBatch(_follow ? StreamState.Following : StreamState.Idle));
                    }

                    await Emit(step.Batch!);
                    if (step.More)
                    {
                        continue; // more is waiting: keep reading without a pause
                    }

                    if (_options.CompleteAtEndOfInitialLoad)
                    {
                        await Emit(reader.CloseBatch(initialLoadComplete: true));
                        return;
                    }

                    if (step.Batch!.InitialLoadComplete)
                    {
                        await Emit(new StatusBatch(_follow ? StreamState.Following : StreamState.Idle));
                    }

                    break;

                case StepKind.Marker:
                    // The first read found nothing (an empty file, or one that is all byte-order mark): the load is still complete.
                    everExisted = true;
                    if (_options.CompleteAtEndOfInitialLoad)
                    {
                        await Emit(reader.CloseBatch(initialLoadComplete: true));
                        return;
                    }

                    await Emit(reader.MarkerBatch());
                    await Emit(new StatusBatch(_follow ? StreamState.Following : StreamState.Idle));
                    break;

                case StepKind.NoData:
                    if (waitingReported)
                    {
                        waitingReported = false; // the file is back (and unchanged)
                        await Emit(new StatusBatch(_follow ? StreamState.Following : StreamState.Idle));
                    }

                    break;
            }

            // Caught up with the file: follow a rolling log to its next file.
            if (step.Kind == StepKind.NoData && _follow && _followRolled && reader.TryFindSuccessor(out var nextPath))
            {
                await Emit(reader.CloseBatch(initialLoadComplete: false));
                await Emit(new SwitchedBatch(nextPath!));
                reader = reader.SwitchTo(nextPath!);
                previewed = false;
                previewDue = null;
                continue;
            }

            // A file that ends mid-line and stops growing: show the last line after a pause.
            if (!previewed && reader.Parser.HasPartialLine && reader.InitialDone)
            {
                previewDue ??= DateTime.UtcNow + _options.QuiescenceDelay;
                if (DateTime.UtcNow >= previewDue)
                {
                    previewed = true;
                    await Emit(reader.PreviewBatch());
                    continue;
                }
            }
            else
            {
                previewDue = null;
            }

            await WaitAsync(NextWait(previewDue), cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan? NextWait(DateTime? previewDue)
    {
        TimeSpan? wait = _follow ? _options.PollInterval : null;
        if (previewDue is { } due)
        {
            var untilPreview = due - DateTime.UtcNow;
            if (untilPreview < TimeSpan.FromMilliseconds(10))
            {
                untilPreview = TimeSpan.FromMilliseconds(10);
            }

            wait = wait is null || untilPreview < wait ? untilPreview : wait;
        }

        return wait;
    }

    private async Task WaitAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (timeout is null)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _wake.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
    }

    private FileSystemWatcher? CreateWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnWatcherEvent;
            watcher.Created += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.EnableRaisingEvents = true;
            return watcher; // on an overflow or error the poll still notices new data
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return null; // polling only
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (_follow)
        {
            Signal();
        }
    }

    // ------------------------------------------------------------------ reading one file

    private enum StepKind { NoData, Data, Marker, Reset, Missing }

    private readonly record struct StepResult(StepKind Kind, ChunkBatch? Batch = null, bool More = false, string? Reason = null, Exception? Error = null);

    /// <summary>The state of reading one file: where we are, how to decode it, and its parser.</summary>
    private sealed class Reader
    {
        private readonly StreamOptions _options;
        private readonly byte[] _buffer;
        private readonly string? _pattern;
        private Decoder? _decoder;
        private long _position;
        private int _nextChunk;
        private byte[] _headSignature = [];

        public Reader(string path, StreamOptions options, ILogParser? parser, bool continuing = false)
        {
            Path = path;
            _options = options;
            _buffer = new byte[Math.Max(options.ChunkBytes, 4)];
            _nextChunk = Math.Clamp(options.FirstChunkBytes, 4, _buffer.Length);
            Parser = parser ?? options.Format.CreateParser(path);
            InitialDone = continuing;
            _pattern = RollPattern.FromFileName(System.IO.Path.GetFileName(path));
        }

        public string Path { get; }

        public ILogParser Parser { get; }

        /// <summary>The existing content has been read once.</summary>
        public bool InitialDone { get; private set; }

        public string EncodingName { get; private set; } = string.Empty;

        public StepResult Step(CancellationToken cancellationToken)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new StepResult(StepKind.Missing, Error: ex);
            }
            catch (IOException) when (_decoder is not null)
            {
                return new StepResult(StepKind.NoData); // briefly locked or mid-rotation: try again next time
            }

            using (stream)
            {
                var length = stream.Length;
                if (_decoder is null)
                {
                    Initialise(stream);
                }
                else if (length < _position)
                {
                    return new StepResult(StepKind.Reset, Reason: "The file got shorter, so it was read again from the start.");
                }
                else if (!HeadMatches(stream))
                {
                    // Looked at again: a writer that truncated it just after the length was read is a shorter file, not a replaced one.
                    return new StepResult(StepKind.Reset, Reason: stream.Length < _position
                        ? "The file got shorter, so it was read again from the start."
                        : "The file was replaced, so it was read again from the start.");
                }

                if (_position >= length)
                {
                    CaptureHeadSignature(stream, length);
                    return InitialDone ? new StepResult(StepKind.NoData) : new StepResult(StepKind.Marker);
                }

                var want = (int)Math.Min(Math.Min(_nextChunk, length - _position), _buffer.Length);
                stream.Seek(_position, SeekOrigin.Begin);
                var read = 0;
                while (read < want)
                {
                    var got = stream.Read(_buffer, read, want - read);
                    if (got == 0)
                    {
                        break; // it shrank under us; the next step notices
                    }

                    read += got;
                }

                CaptureHeadSignature(stream, length);
                var lengthNow = stream.Length;

                cancellationToken.ThrowIfCancellationRequested();
                var chars = new char[_decoder!.GetCharCount(_buffer, 0, read, flush: false)];
                var text = new string(chars, 0, _decoder.GetChars(_buffer, 0, read, chars, 0, flush: false));
                var delta = Parser.Append(text, cancellationToken);
                _position += read;
                _nextChunk = (int)Math.Min((long)_nextChunk * 4, _buffer.Length);

                var more = _position < lengthNow;
                var complete = !more && !InitialDone && !_options.CompleteAtEndOfInitialLoad;
                if (!more)
                {
                    InitialDone = true;
                }

                var batch = new ChunkBatch(text, delta, _position, Math.Max(length, _position), complete, Path, EncodingName);
                return new StepResult(StepKind.Data, batch, more);
            }
        }

        /// <summary>An empty batch that ends the first read without changing anything (the file had nothing to read).</summary>
        public ChunkBatch MarkerBatch()
        {
            InitialDone = true;
            return new ChunkBatch(string.Empty, new ParseDelta(Parser.CommittedId, []), _position, _position, true, Path, EncodingName);
        }

        /// <summary>Makes everything held back final (the end of a finished file) and returns the closing delta.</summary>
        public ChunkBatch CloseBatch(bool initialLoadComplete)
        {
            InitialDone = true;
            return new ChunkBatch(string.Empty, Parser.Complete(), _position, _position, initialLoadComplete, Path, EncodingName);
        }

        /// <summary>The events as they would be if the file ended now, an unterminated last line included.</summary>
        public ChunkBatch PreviewBatch() => new(string.Empty, Parser.Preview(), _position, _position, false, Path, EncodingName);

        public bool TryFindSuccessor(out string? nextPath)
        {
            nextPath = null;
            if (_pattern is null)
            {
                return false;
            }

            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path)!;
                var candidates = Directory.EnumerateFiles(directory, _pattern)
                    .Select(f => new RollCandidate(System.IO.Path.GetFileName(f), File.GetCreationTimeUtc(f)));
                var next = RollPattern.PickSuccessor(_pattern, System.IO.Path.GetFileName(Path), File.GetCreationTimeUtc(Path), candidates);
                if (next is null)
                {
                    return false;
                }

                nextPath = System.IO.Path.Combine(directory, next);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Starts reading <paramref name="nextPath"/> from its beginning, continuing the event ids and text offsets.</summary>
        public Reader SwitchTo(string nextPath)
            => new(nextPath, _options, _options.Format.CreateParser(nextPath, Parser.EndOffset, Parser.CommittedId), continuing: true);

        private void Initialise(FileStream stream)
        {
            var head = new byte[4];
            var (encoding, preamble, name) = TextEncodings.Detect(head.AsSpan(0, ReadAt(stream, 0, head)));
            _decoder = encoding.GetDecoder();
            EncodingName = name;
            _position = preamble;
        }

        // ---- truncation / replacement detection ----

        private bool HeadMatches(FileStream stream)
        {
            if (_headSignature.Length == 0)
            {
                return true;
            }

            var current = new byte[_headSignature.Length];
            return ReadAt(stream, 0, current) == _headSignature.Length && current.AsSpan().SequenceEqual(_headSignature);
        }

        private void CaptureHeadSignature(FileStream stream, long length)
        {
            var wanted = (int)Math.Min(HeadSignatureBytes, length);
            if (wanted <= _headSignature.Length)
            {
                return;
            }

            var signature = new byte[wanted];
            if (ReadAt(stream, 0, signature) == wanted
                && (_headSignature.Length == 0 || signature.AsSpan(0, _headSignature.Length).SequenceEqual(_headSignature)))
            {
                _headSignature = signature;
            }
        }

        private static int ReadAt(FileStream stream, long position, byte[] buffer)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var total = 0;
            while (total < buffer.Length)
            {
                var got = stream.Read(buffer, total, buffer.Length - total);
                if (got == 0)
                {
                    break;
                }

                total += got;
            }

            return total;
        }
    }
}

/// <summary>FR-F6: how a log's bytes are decoded.</summary>
internal static class TextEncodings
{
    /// <summary>
    /// UTF-8 (with or without BOM) by default, UTF-16 when its BOM says so. Invalid UTF-8 bytes decode to U+FFFD rather than failing: a damaged
    /// byte must never stop a log from opening or a tail from following it.
    /// </summary>
    public static (Encoding Encoding, int PreambleLength, string Name) Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(false), 3, "UTF-8 with BOM");
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2, "UTF-16 LE");
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2, "UTF-16 BE");
        }

        return (new UTF8Encoding(false), 0, "UTF-8");
    }
}
