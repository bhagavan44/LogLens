using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using LogLens.Core.Formats;
using LogLens.Core.Remote;
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

    /// <summary>Shortest time between two looks for the next file of a rolling log while following. Zero looks on every poll; remote folders are listed less often.</summary>
    public TimeSpan RollCheckInterval { get; init; } = TimeSpan.Zero;

    /// <summary>How long to wait before trying an unreachable remote host again (FR-O7).</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Batch mode (<see cref="LogFileLoader"/>): once the existing content is read, make everything final and end the stream.</summary>
    public bool CompleteAtEndOfInitialLoad { get; init; }

    /// <summary>
    /// The options for a file on an SSH host: polled every second (SFTP has no change events) and its folder listed for the next file of a rolling
    /// log at most every 10 s.
    /// </summary>
    public StreamOptions ForRemote() => this with
    {
        UseWatcher = false,
        PollInterval = TimeSpan.FromSeconds(1),
        RollCheckInterval = TimeSpan.FromSeconds(10),
    };
}

/// <summary>Opens <see cref="FileLogStream"/>s on local files, Windows shares and (once their host is connected) <c>sftp://</c> files.</summary>
/// <param name="options">How files are read; its <see cref="StreamOptions.Format"/> is used when no format is given and there is no catalog.</param>
/// <param name="formats">
/// The formats in force (built-in plus custom templates, which can change while the app runs); when given, a file opened without a format has
/// it detected among them (FR-F1).
/// </param>
/// <param name="remoteFileSystem">Reads an <c>sftp://</c> file through its host's connection, or null when that host isn't connected (FR-O7).</param>
public sealed class FileLogStreamFactory(
    StreamOptions? options = null,
    Func<FormatCatalog>? formats = null,
    Func<string, ILogFileSystem?>? remoteFileSystem = null) : ILogStreamFactory
{
    public ILogStream Open(string path, ILogFormat? format = null)
    {
        var fileSystem = FileSystemFor(path);
        var streamOptions = (options ?? new StreamOptions()) with { Format = FormatFor(format) };
        return new FileLogStream(path, fileSystem is LocalLogFileSystem ? streamOptions : streamOptions.ForRemote(), fileSystem);
    }

    public ILogStream OpenMerged(IReadOnlyList<string> paths, ILogFormat? format = null) => new MergedLogStream(paths, FormatFor(format), FileSystemFor);

    /// <summary>Local files and shares are read by Windows; an <c>sftp://</c> file needs its host connected first.</summary>
    /// <exception cref="RemoteUnavailableException">The file's host isn't connected.</exception>
    private ILogFileSystem FileSystemFor(string path)
        => !RemoteLocation.IsRemote(path)
            ? LocalLogFileSystem.Instance
            : remoteFileSystem?.Invoke(path) ?? throw new RemoteUnavailableException($"Not connected to the host of {RemoteLocation.FileName(path)}.");

    private ILogFormat FormatFor(ILogFormat? format)
        => format ?? (formats is not null ? new AutoDetectFormat(formats()) : options?.Format ?? LogFormats.Default);
}

/// <summary>
/// Reads a log file in chunks and, while <see cref="Follow"/> is on, keeps following it (SPEC §6.3). The file is opened with
/// <c>FileShare.ReadWrite | FileShare.Delete</c> for each read and closed again, so the app writing it — or a log rotator — is never blocked
/// (FR-O5); the viewer only reads. The file can be local (or on a share) or on an SSH host: an <see cref="ILogFileSystem"/> does the reading.
/// </summary>
/// <remarks>
/// Reads the existing content in growing chunks; then, while following, wakes on file-system events or every poll interval and delivers what
/// was appended (FR-T1). A shorter file, or one whose first bytes changed, was truncated or replaced: reported with a <see cref="ResetBatch"/>
/// and read again (FR-T3). With <see cref="FollowRolled"/> it moves on to the next file of a rolling log (<see cref="RollPattern"/>) once the
/// current one is drained. An unfinished last line is held back until its newline arrives (FR-T6), or shown once the file has been quiet
/// for <see cref="StreamOptions.QuiescenceDelay"/>. A remote host that can't be reached shows <see cref="StreamState.Reconnecting"/> and is tried
/// again every <see cref="StreamOptions.ReconnectDelay"/>; reading resumes where it stopped (FR-O7). All work runs on the thread pool.
/// </remarks>
public sealed class FileLogStream : ILogStream
{
    private const int HeadSignatureBytes = 256;

    private readonly string _path;
    private readonly StreamOptions _options;
    private readonly ILogFileSystem _fs;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _cts;
    private volatile bool _follow;
    private volatile bool _followRolled = true;
    private volatile bool _disposed;

    /// <param name="path">A local or UNC path, or an <c>sftp://</c> location read through <paramref name="fileSystem"/>.</param>
    /// <param name="options">How the file is read.</param>
    /// <param name="fileSystem">Where the file is read from; the local file system when null.</param>
    public FileLogStream(string path, StreamOptions? options = null, ILogFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fs = fileSystem ?? LocalLogFileSystem.Instance;
        _path = _fs is LocalLogFileSystem ? Path.GetFullPath(path) : path;
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

        using var watcher = _options.UseWatcher && _fs.SupportsWatching && !_options.CompleteAtEndOfInitialLoad ? CreateWatcher() : null;
        var reader = new Reader(_fs, _path, _options, parser: null);
        var everExisted = false;
        var waitingReported = false;
        var unavailable = false;
        var previewed = false;
        DateTime? previewDue = null;

        await Emit(new StatusBatch(StreamState.Loading));

        while (!_disposed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StepResult step;
            try
            {
                step = reader.Step(cancellationToken);
            }
            catch (RemoteUnavailableException ex) when (!_options.CompleteAtEndOfInitialLoad) // a batch load (merge) fails instead of waiting
            {
                step = new StepResult(StepKind.Unavailable, Reason: ex.Message);
            }

            if (unavailable && step.Kind != StepKind.Unavailable)
            {
                unavailable = false; // reachable again: say what the stream is doing now
                await Emit(new StatusBatch(!reader.InitialDone ? StreamState.Loading : _follow ? StreamState.Following : StreamState.Idle));
            }

            switch (step.Kind)
            {
                case StepKind.Unavailable:
                    if (!unavailable)
                    {
                        unavailable = true;
                        await Emit(new StatusBatch(StreamState.Reconnecting, step.Reason));
                    }

                    await WaitAsync(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
                    continue;

                case StepKind.Missing:
                    if (!everExisted && !reader.InitialDone)
                    {
                        throw step.Error!; // the file to open is not there: report it the way File.Open would
                    }

                    if (!waitingReported)
                    {
                        waitingReported = true;
                        await Emit(new StatusBatch(StreamState.WaitingForFile, $"Waiting for {FileName(reader.Path)}…"));
                    }

                    break;

                case StepKind.Reset:
                    everExisted = true;
                    waitingReported = false;
                    await Emit(new ResetBatch(step.Reason!));
                    reader = new Reader(_fs, reader.Path, _options, parser: null);
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

    private static string FileName(string path) => RemoteLocation.IsRemote(path) ? RemoteLocation.FileName(path) : Path.GetFileName(path);

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

    private enum StepKind { NoData, Data, Marker, Reset, Missing, Unavailable }

    private readonly record struct StepResult(StepKind Kind, ChunkBatch? Batch = null, bool More = false, string? Reason = null, Exception? Error = null);

    /// <summary>The state of reading one file: where we are, how to decode it, and its parser.</summary>
    private sealed class Reader
    {
        private readonly ILogFileSystem _fs;
        private readonly StreamOptions _options;
        private readonly byte[] _buffer;
        private readonly string? _pattern;
        private readonly ILogFormat _format;
        private Decoder? _decoder;
        private long _position;
        private int _nextChunk;
        private byte[] _headSignature = [];
        private DateTime _lastRollCheck = DateTime.MinValue;

        public Reader(ILogFileSystem fs, string path, StreamOptions options, ILogParser? parser, bool continuing = false, ILogFormat? format = null)
        {
            _fs = fs;
            _format = format ?? options.Format;
            Path = path;
            _options = options;
            _buffer = new byte[Math.Max(options.ChunkBytes, 4)];
            _nextChunk = Math.Clamp(options.FirstChunkBytes, 4, _buffer.Length);
            Parser = parser ?? options.Format.CreateParser(path);
            InitialDone = continuing;
            _pattern = RollPattern.FromFileName(FileName(path));
        }

        public string Path { get; }

        public ILogParser Parser { get; }

        /// <summary>The format the text is parsed with; null while it is still being detected (FR-F1).</summary>
        public ILogFormat? ParsedFormat => Parser is DetectingParser detecting ? detecting.Detected : _format;

        /// <summary>The existing content has been read once.</summary>
        public bool InitialDone { get; private set; }

        public string EncodingName { get; private set; } = string.Empty;

        public StepResult Step(CancellationToken cancellationToken)
        {
            Stream stream;
            try
            {
                stream = _fs.OpenRead(Path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new StepResult(StepKind.Missing, Error: ex);
            }
            catch (IOException ex) when (_decoder is not null && ex is not RemoteUnavailableException)
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

                // All reading is done before anything is committed: a remote read that fails here leaves the reader as it was, so the same
                // chunk is simply read again once the host is back.
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

                var batch = new ChunkBatch(text, delta, _position, Math.Max(length, _position), complete, Path, EncodingName, Format: ParsedFormat);
                return new StepResult(StepKind.Data, batch, more);
            }
        }

        /// <summary>An empty batch that ends the first read without changing anything (the file had nothing to read).</summary>
        public ChunkBatch MarkerBatch()
        {
            InitialDone = true;
            return new ChunkBatch(string.Empty, new ParseDelta(Parser.CommittedId, []), _position, _position, true, Path, EncodingName, Format: ParsedFormat);
        }

        /// <summary>Makes everything held back final (the end of a finished file) and returns the closing delta.</summary>
        public ChunkBatch CloseBatch(bool initialLoadComplete)
        {
            InitialDone = true;
            var delta = Parser.Complete();
            return new ChunkBatch(string.Empty, delta, _position, _position, initialLoadComplete, Path, EncodingName, Format: ParsedFormat);
        }

        /// <summary>The events as they would be if the file ended now, an unterminated last line included.</summary>
        public ChunkBatch PreviewBatch()
        {
            var delta = Parser.Preview();
            return new(string.Empty, delta, _position, _position, false, Path, EncodingName, Format: ParsedFormat);
        }

        public bool TryFindSuccessor(out string? nextPath)
        {
            nextPath = null;
            if (_pattern is null)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (_options.RollCheckInterval > TimeSpan.Zero && now - _lastRollCheck < _options.RollCheckInterval)
            {
                return false;
            }

            _lastRollCheck = now;
            try
            {
                var directory = _fs.GetFolder(Path)!;
                var candidates = _fs.ListFiles(directory).Where(c => RollPattern.Matches(_pattern, c.Name)).ToList();
                var next = RollPattern.PickSuccessor(_pattern, FileName(Path), _fs.GetCreatedUtc(Path), candidates);
                if (next is null)
                {
                    return false;
                }

                nextPath = _fs.Combine(directory, next);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Starts reading <paramref name="nextPath"/> from its beginning, continuing the event ids and text offsets. The next file of a rolling
        /// log is written by the same app, so it is read with the format detected for this one.
        /// </summary>
        public Reader SwitchTo(string nextPath)
        {
            var format = ParsedFormat ?? _options.Format;
            return new(_fs, nextPath, _options, format.CreateParser(nextPath, Parser.EndOffset, Parser.CommittedId), continuing: true, format);
        }

        private void Initialise(Stream stream)
        {
            var head = new byte[4];
            var (encoding, preamble, name) = TextEncodings.Detect(head.AsSpan(0, ReadAt(stream, 0, head)));
            _decoder = encoding.GetDecoder();
            EncodingName = name;
            _position = preamble;
        }

        // ---- truncation / replacement detection ----

        private bool HeadMatches(Stream stream)
        {
            if (_headSignature.Length == 0)
            {
                return true;
            }

            var current = new byte[_headSignature.Length];
            return ReadAt(stream, 0, current) == _headSignature.Length && current.AsSpan().SequenceEqual(_headSignature);
        }

        private void CaptureHeadSignature(Stream stream, long length)
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

        private static int ReadAt(Stream stream, long position, byte[] buffer)
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
