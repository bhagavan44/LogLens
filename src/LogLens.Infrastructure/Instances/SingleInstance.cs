using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogLens.Infrastructure.Instances;

/// <summary>What a second launch asks the running instance to do: open the files on its command line, read relative to its folder.</summary>
public sealed record LaunchRequest(string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    /// <summary>Most arguments a single request may carry; anything longer is a mistake or abuse.</summary>
    public const int MaxArguments = 200;

    /// <summary>The files to open, as full paths (options and their values left out, see <see cref="CommandLine"/>).</summary>
    public IReadOnlyList<string> ResolvePaths() => Resolve().Files;

    /// <summary>The handed-over command line, read as if this process had been started with it.</summary>
    public CommandLine Resolve() => CommandLine.Parse(Arguments, WorkingDirectory);
}

/// <summary>
/// Keeps one LogLens per user, Windows session and profile (<c>--data-dir</c>): a launch while another instance runs hands its command line to
/// that instance (which opens the files and comes to the front) and exits. Election uses a session-local mutex; requests travel over a named pipe
/// that only the same user can open (<see cref="PipeOptions.CurrentUserOnly"/>). If the running instance does not answer in time the
/// new process simply starts as a normal, separate instance — a hung window never stops the user from opening a log.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 256 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<LaunchRequest> _pending = [];
    private Action<LaunchRequest>? _handler;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Becomes the primary instance for <paramref name="key"/>, or forwards <paramref name="request"/> to the instance that already is.
    /// Returns the primary's <see cref="SingleInstance"/> (start listening with <see cref="Listen"/>), or null when the request was
    /// delivered and this process should exit. <paramref name="allowForeground"/> receives the primary's process id just before the
    /// request is sent, so the caller can let it take the foreground (Windows only lets the foreground process grant that).
    /// </summary>
    public static SingleInstance? Claim(string key, LaunchRequest request, Action<int>? allowForeground = null)
    {
        var names = Names(key);
        var mutex = new Mutex(initiallyOwned: true, names.Mutex, out var createdNew);
        if (!createdNew)
        {
            // Wait briefly in case the previous primary is just exiting (an abandoned mutex is fine: it means that process ended).
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                if (TrySend(names.Pipe, request, allowForeground))
                {
                    mutex.Dispose();
                    return null;
                }

                // The running instance did not answer: run on our own, without taking over the channel.
                mutex.Dispose();
                return new SingleInstance(new Mutex(), names.Pipe + "-orphan-" + Environment.ProcessId) { IsPrimary = false };
            }
        }

        var instance = new SingleInstance(mutex, names.Pipe) { IsPrimary = true };
        instance.StartServer();
        return instance;
    }

    /// <summary>True when this process owns the channel (other launches are forwarded to it).</summary>
    public bool IsPrimary { get; private init; }

    /// <summary>
    /// Sets the handler for forwarded launches; requests that arrived before (while the window was starting) are delivered now. The handler
    /// runs on a worker thread: marshal to the UI thread yourself.
    /// </summary>
    public void Listen(Action<LaunchRequest> handler)
    {
        List<LaunchRequest> queued;
        lock (_gate)
        {
            _handler = handler;
            queued = [.. _pending];
            _pending.Clear();
        }

        foreach (var request in queued)
        {
            handler(request);
        }
    }

    /// <summary>The mutex and pipe names for <paramref name="key"/> in this user's current Windows session.</summary>
    internal static (string Mutex, string Pipe) Names(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..16];
        var session = Process.GetCurrentProcess().SessionId;
        return ($@"Local\LogLens-{hash}", $"LogLens-{session}-{hash}");
    }

    private void StartServer()
    {
        if (!IsPrimary)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var connection = server;
                    server = null;
                    _ = Task.Run(() => ServeAsync(connection));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(250).ConfigureAwait(false); // e.g. a client that disconnected at once; keep listening
                }
                finally
                {
                    server?.Dispose();
                }
            }
        });
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(ConnectTimeout);

                // Hello: our process id, so the client can allow us to take the foreground before it sends anything.
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

                var request = await ReadRequestAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                // Queue (or hand over) first, then acknowledge: once the sender exits, the request must not be lost.
                Deliver(request);
                pipe.WriteByte(1);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException)
            {
                // a broken or malformed request is dropped; the sender falls back to starting its own window
            }
        }
    }

    private void Deliver(LaunchRequest request)
    {
        Action<LaunchRequest>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _pending.Add(request);
                return;
            }
        }

        handler(request);
    }

    private static async Task<LaunchRequest?> ReadRequestAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            return null;
        }

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<Message>(body);
        if (message?.Cwd is null || message.Args is null || message.Args.Length > LaunchRequest.MaxArguments)
        {
            return null;
        }

        return new LaunchRequest(message.Cwd, message.Args);
    }

    private static bool TrySend(string pipeName, LaunchRequest request, Action<int>? allowForeground)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);

            var hello = new byte[4];
            pipe.ReadExactly(hello);
            allowForeground?.Invoke(BitConverter.ToInt32(hello));

            var body = JsonSerializer.SerializeToUtf8Bytes(new Message(request.WorkingDirectory, [.. request.Arguments.Take(LaunchRequest.MaxArguments)]));
            pipe.Write(BitConverter.GetBytes(body.Length));
            pipe.Write(body);
            pipe.Flush();

            return pipe.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (IsPrimary)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // released already, or called from another thread at shutdown
        }

        _mutex.Dispose();
        _stop.Dispose();
    }

    private sealed record Message(string Cwd, string[] Args);
}
