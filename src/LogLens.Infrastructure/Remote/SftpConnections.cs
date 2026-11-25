using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
using LogLens.Core.Remote;
using LogLens.Infrastructure.Loading;

namespace LogLens.Infrastructure.Remote;

/// <summary>Where to connect and as whom. <see cref="KeyFile"/> empty means "try the usual keys in <c>~/.ssh</c>".</summary>
public sealed record SshTarget(string Host, int Port, string UserName, string KeyFile, string DisplayName)
{
    public static SshTarget From(RemoteHost host)
        => new(host.Address, host.Port, host.UserName, host.KeyFile, host.DisplayName);

    public string ConnectionKey => RemoteLocation.ConnectionKeyOf(UserName, Host, Port);

    public override string ToString() => RemoteLocation.Authority(UserName, Host, Port);
}

/// <summary>A host key the server presented that isn't trusted yet.</summary>
/// <param name="KeyType">The key's algorithm, e.g. <c>ssh-ed25519</c>.</param>
/// <param name="Fingerprint">OpenSSH-style <c>SHA256:…</c> fingerprint, to compare with <c>ssh-keygen -lf</c> on the server.</param>
/// <param name="KeyBase64">The public key itself.</param>
public sealed record HostKeyInfo(string KeyType, string Fingerprint, string KeyBase64);

/// <summary>
/// Asks the user for what connecting needs. Every method returns null / false when the user cancels. Called on the thread that called
/// <see cref="SftpConnections.ConnectAsync"/> (the UI thread).
/// </summary>
public interface ISshPrompt
{
    /// <param name="problem">Why it is asked again (e.g. the last password was refused), or null the first time.</param>
    Task<string?> AskPasswordAsync(SshTarget target, string? problem, CancellationToken cancellationToken);

    Task<string?> AskPassphraseAsync(SshTarget target, string keyFile, string? problem, CancellationToken cancellationToken);

    /// <summary>The host isn't known yet: show the fingerprint and ask whether to trust it.</summary>
    Task<bool> ConfirmHostKeyAsync(SshTarget target, HostKeyInfo key, CancellationToken cancellationToken);
}

/// <summary>Connecting failed for a reason the user should read (host unreachable, key changed, key file unusable …).</summary>
public sealed class SshConnectFailedException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The open SSH connections, one per <c>user@host:port</c>, shared by every tab, merge and folder listing that reads from that login.
/// <see cref="ConnectAsync"/> does the interactive part (host key, passphrases, password) once; afterwards a dropped connection is
/// re-established quietly with the same credentials, which are held in memory only and never written anywhere.
/// </summary>
public sealed class SftpConnections : IDisposable
{
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    private readonly Dictionary<string, SftpConnection> _connections = new(StringComparer.Ordinal);
    private readonly IHostKeyTrust _trust;
    private readonly string _sshFolder;
    private readonly ILogger _log;
    private readonly object _gate = new();

    /// <param name="sshFolder">Where the user's default keys live (<c>%USERPROFILE%\.ssh</c>).</param>
    public SftpConnections(IHostKeyTrust trust, string sshFolder, ILogger? log = null)
    {
        _trust = trust;
        _sshFolder = sshFolder;
        _log = log ?? Serilog.Core.Logger.None;
    }

    /// <summary>Time allowed for the TCP connection and SSH handshake.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The live connection for <paramref name="connectionKey"/> (see <see cref="RemoteLocation.ConnectionKey"/>), if any.</summary>
    public SftpConnection? Find(string connectionKey)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionKey, out var c) && !c.IsClosed ? c : null;
        }
    }

    /// <summary>Reads <c>sftp://</c> files through their host's connection; null when that host isn't connected (call <see cref="ConnectAsync"/> first).</summary>
    public ILogFileSystem? FileSystemFor(string path)
        => RemoteLocation.TryParse(path, out var location) && Find(location.ConnectionKey) is { } connection
            ? new SftpLogFileSystem(connection)
            : null;

    /// <summary>
    /// Connects (or returns the existing connection): checks the host key — asking the user about an unknown one and refusing a changed
    /// one — then signs in with the key file or the usual keys, asking for passphrases and finally a password as needed.
    /// </summary>
    /// <exception cref="OperationCanceledException">The user cancelled a prompt, or <paramref name="cancellationToken"/> fired.</exception>
    /// <exception cref="SshConnectFailedException">The host can't be reached, its key changed, or a key file can't be used.</exception>
    public async Task<SftpConnection> ConnectAsync(SshTarget target, ISshPrompt prompt, CancellationToken cancellationToken = default)
    {
        if (Find(target.ConnectionKey) is { } existing)
        {
            return existing;
        }

        var credentials = new Credentials();
        var encryptedDefaults = LoadKeys(target, credentials);
        if (target.KeyFile.Length > 0 && credentials.Keys.Count == 0)
        {
            // The chosen key is encrypted: ask for its passphrase up front.
            await AddEncryptedKeyAsync(target, target.KeyFile, credentials, prompt, required: true, cancellationToken);
        }

        var known = _trust.Snapshot();
        string? passwordProblem = null;
        var triedEncryptedDefaults = encryptedDefaults.Count == 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await Task.Run(() => Attempt(target, credentials, known), cancellationToken);

            switch (outcome)
            {
                case Connected connected:
                    var connection = new SftpConnection(target, connected.Client, credentials, known, this, _log);
                    lock (_gate)
                    {
                        _connections[target.ConnectionKey] = connection;
                    }

                    _log.Information("Connected to {Target} over SFTP", target.ToString());
                    return connection;

                case UnknownHostKey unknown:
                    if (!await prompt.ConfirmHostKeyAsync(target, unknown.Key, cancellationToken))
                    {
                        throw new OperationCanceledException("The host key was not accepted.");
                    }

                    _trust.Trust(target.Host, target.Port, unknown.Key.KeyBase64);
                    known.Add(KnownHosts.HostToken(target.Host, target.Port), unknown.Key.KeyType, unknown.Key.KeyBase64);
                    _log.Information("Trusted the {Type} host key of {Target} ({Fingerprint})", unknown.Key.KeyType, target.ToString(), unknown.Key.Fingerprint);
                    continue;

                case RefusedHostKey refused:
                    throw new SshConnectFailedException(refused.Message);

                case AuthenticationFailed failed:
                    _log.Information("Sign-in to {Target} was refused: {Reason}", target.ToString(), failed.Reason);
                    if (!triedEncryptedDefaults)
                    {
                        triedEncryptedDefaults = true;
                        foreach (var keyFile in encryptedDefaults)
                        {
                            await AddEncryptedKeyAsync(target, keyFile, credentials, prompt, required: false, cancellationToken);
                        }

                        if (credentials.Keys.Count > 0)
                        {
                            continue;
                        }
                    }

                    var password = await prompt.AskPasswordAsync(target, passwordProblem, cancellationToken)
                        ?? throw new OperationCanceledException("No password was given.");
                    credentials.Password = password;
                    passwordProblem = "That password was not accepted. Try again.";
                    continue;

                case Failed error:
                    throw new SshConnectFailedException(error.Message, error.Exception);
            }
        }
    }

    /// <summary>Closes the connection for <paramref name="connectionKey"/>; files read through it show "Reconnecting…" until it is connected again.</summary>
    public void Disconnect(string connectionKey)
    {
        SftpConnection? connection;
        lock (_gate)
        {
            if (!_connections.Remove(connectionKey, out connection))
            {
                return;
            }
        }

        connection.Close();
    }

    public void Dispose()
    {
        List<SftpConnection> all;
        lock (_gate)
        {
            all = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var c in all)
        {
            c.Close();
        }
    }

    // ------------------------------------------------------------------ keys

    internal sealed class Credentials
    {
        public List<IPrivateKeySource> Keys { get; } = [];

        public string? Password { get; set; }
    }

    /// <summary>Loads the chosen key, or every usual key that needs no passphrase. Returns the usual keys that are encrypted (for later).</summary>
    private List<string> LoadKeys(SshTarget target, Credentials credentials)
    {
        var encrypted = new List<string>();
        if (target.KeyFile.Length > 0)
        {
            if (!File.Exists(target.KeyFile))
            {
                throw new SshConnectFailedException($"The key file {target.KeyFile} doesn't exist.");
            }

            try
            {
                credentials.Keys.Add(new PrivateKeyFile(target.KeyFile));
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                // encrypted: the caller asks for the passphrase
            }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
            {
                throw new SshConnectFailedException($"The key file {Path.GetFileName(target.KeyFile)} can't be used: {ex.Message}", ex);
            }

            return encrypted;
        }

        foreach (var name in DefaultKeyNames)
        {
            var path = Path.Combine(_sshFolder, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                credentials.Keys.Add(new PrivateKeyFile(path));
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                encrypted.Add(path);
            }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
            {
                _log.Information(ex, "Skipped the key {Key}, which can't be used", name);
            }
        }

        return encrypted;
    }

    private static async Task AddEncryptedKeyAsync(SshTarget target, string keyFile, Credentials credentials, ISshPrompt prompt, bool required, CancellationToken cancellationToken)
    {
        string? problem = null;
        while (true)
        {
            var passphrase = await prompt.AskPassphraseAsync(target, keyFile, problem, cancellationToken);
            if (passphrase is null)
            {
                if (required)
                {
                    throw new OperationCanceledException("No passphrase was given.");
                }

                return; // skip this key
            }

            try
            {
                credentials.Keys.Add(new PrivateKeyFile(keyFile, passphrase));
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problem = "That passphrase didn't unlock the key. Try again.";
            }
        }
    }

    // ------------------------------------------------------------------ one connection attempt (worker thread)

    private abstract record Outcome;

    private sealed record Connected(SftpClient Client) : Outcome;

    private sealed record UnknownHostKey(HostKeyInfo Key) : Outcome;

    private sealed record RefusedHostKey(string Message) : Outcome;

    private sealed record AuthenticationFailed(string Reason) : Outcome;

    private sealed record Failed(string Message, Exception Exception) : Outcome;

    /// <summary>Connects once with what is known so far. Never prompts; the caller decides what to ask next from the outcome.</summary>
    private Outcome Attempt(SshTarget target, Credentials credentials, KnownHosts known)
    {
        var methods = new List<AuthenticationMethod>();
        if (credentials.Keys.Count > 0)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(target.UserName, [.. credentials.Keys]));
        }

        if (credentials.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(target.UserName, password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(target.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts)
                {
                    p.Response = password; // PAM's "Password:" prompt
                }
            };
            methods.Add(interactive);
        }

        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(target.UserName)); // still checks the host key before a password is asked for
        }

        var info = new ConnectionInfo(target.Host, target.Port, target.UserName, [.. methods]) { Timeout = ConnectTimeout };
        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromSeconds(30) };
        HostKeyInfo? presented = null;
        var status = HostKeyStatus.Unknown;
        client.HostKeyReceived += (_, e) =>
        {
            var key = Convert.ToBase64String(e.HostKey);
            presented = new HostKeyInfo(KnownHosts.KeyTypeOf(key) ?? e.HostKeyName, KnownHosts.Fingerprint(key), key);
            status = known.Check(target.Host, target.Port, key);
            e.CanTrust = status == HostKeyStatus.Trusted;
        };

        try
        {
            client.Connect();
            return new Connected(client);
        }
        catch (Exception ex) when (presented is not null && status != HostKeyStatus.Trusted && ex is SshConnectionException)
        {
            client.Dispose();
            return status switch
            {
                HostKeyStatus.Unknown => new UnknownHostKey(presented),
                HostKeyStatus.Revoked => new RefusedHostKey($"The host key of {target.Host} is marked as revoked in known_hosts, so LogLens won't connect."),
                _ => new RefusedHostKey(
                    $"The host key of {target.Host} has changed ({presented.Fingerprint}). This can mean someone is intercepting the connection, " +
                    "so LogLens won't connect. If the server was reinstalled, remove its old key (from ~/.ssh/known_hosts, or with Forget host key in the host's settings) and connect again."),
            };
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            return new AuthenticationFailed(ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException or ProxyException or SshException or InvalidOperationException)
        {
            client.Dispose();
            return new Failed(Describe(target, ex), ex);
        }
    }

    /// <summary>Re-establishes a dropped connection with the credentials and host keys from the first connect. Worker thread; no prompts.</summary>
    internal SftpClient Reconnect(SshTarget target, Credentials credentials, KnownHosts known)
    {
        return Attempt(target, credentials, known) switch
        {
            Connected c => c.Client,
            Failed f => throw new RemoteUnavailableException(f.Message, f.Exception),
            AuthenticationFailed a => throw new RemoteUnavailableException($"{target} no longer accepts the sign-in: {a.Reason}"),
            RefusedHostKey r => throw new RemoteUnavailableException(r.Message),
            UnknownHostKey => throw new RemoteUnavailableException($"The host key of {target.Host} changed since LogLens connected."),
            _ => throw new RemoteUnavailableException($"Couldn't reconnect to {target}."),
        };
    }

    internal static string Describe(SshTarget target, Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => $"The host name {target.Host} couldn't be found.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => $"{target.Host} refused the connection on port {target.Port}. Is an SSH server running there?",
        SocketException { SocketErrorCode: SocketError.TimedOut } or SshOperationTimeoutException => $"{target.Host} didn't answer in time.",
        SocketException s => $"Couldn't reach {target.Host}:{target.Port}: {s.Message}",
        _ => $"Couldn't connect to {target}: {ex.Message}",
    };

    internal void Forget(SftpConnection connection)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connection.Target.ConnectionKey, out var c) && ReferenceEquals(c, connection))
            {
                _connections.Remove(connection.Target.ConnectionKey);
            }
        }
    }
}

/// <summary>
/// One SSH login with an SFTP session. Thread-safe: reads from several tabs run at the same time over the one session. When the connection
/// drops, the next operation reconnects (at most every few seconds); until then operations fail with <see cref="RemoteUnavailableException"/>.
/// </summary>
public sealed class SftpConnection
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(2);

    private readonly SftpConnections.Credentials _credentials;
    private readonly KnownHosts _known;
    private readonly SftpConnections _owner;
    private readonly ILogger _log;
    private readonly object _reconnectGate = new();
    private SftpClient _client;
    private bool _broken;
    private DateTime _lastFailureUtc = DateTime.MinValue;

    internal SftpConnection(SshTarget target, SftpClient client, SftpConnections.Credentials credentials, KnownHosts known, SftpConnections owner, ILogger log)
    {
        Target = target;
        _client = client;
        _credentials = credentials;
        _known = known;
        _owner = owner;
        _log = log;
        HomeFolder = SafeWorkingDirectory(client);
    }

    public SshTarget Target { get; }

    /// <summary>The user's home folder on the host (where SFTP starts).</summary>
    public string HomeFolder { get; }

    internal bool IsClosed { get; private set; }

    /// <summary>Runs <paramref name="operation"/> on the session, reconnecting first if the connection was lost.</summary>
    /// <exception cref="RemoteUnavailableException">The host can't be reached.</exception>
    /// <exception cref="FileNotFoundException">The path doesn't exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Permission denied.</exception>
    public T Run<T>(Func<SftpClient, T> operation)
    {
        var client = EnsureConnected();
        try
        {
            return operation(client);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            if (translated is RemoteUnavailableException)
            {
                _broken = true;
            }

            throw translated;
        }
    }

    private SftpClient EnsureConnected()
    {
        if (IsClosed)
        {
            throw new RemoteUnavailableException($"The connection to {Target} was closed.");
        }

        var client = _client;
        if (client.IsConnected && !_broken)
        {
            return client;
        }

        lock (_reconnectGate)
        {
            if (_client.IsConnected && !_broken)
            {
                return _client; // another thread reconnected meanwhile
            }

            if (DateTime.UtcNow - _lastFailureUtc < RetryAfterFailure)
            {
                throw new RemoteUnavailableException($"Lost the connection to {Target}.");
            }

            try
            {
                _log.Information("Reconnecting to {Target}", Target.ToString());
                var fresh = _owner.Reconnect(Target, _credentials, _known);
                var old = _client;
                _client = fresh;
                _broken = false;
                Dispose(old);
                _log.Information("Reconnected to {Target}", Target.ToString());
                return fresh;
            }
            catch (RemoteUnavailableException)
            {
                _lastFailureUtc = DateTime.UtcNow;
                throw;
            }
        }
    }

    /// <summary>SFTP and network errors as the exceptions the readers expect; null for anything else (left to propagate).</summary>
    internal Exception? Translate(Exception ex) => ex switch
    {
        SftpPathNotFoundException => new FileNotFoundException(ex.Message, ex),
        SftpPermissionDeniedException => new UnauthorizedAccessException(ex.Message, ex),
        SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException
            => new RemoteUnavailableException($"Lost the connection to {Target}.", ex),
        InvalidOperationException when !_client.IsConnected => new RemoteUnavailableException($"Lost the connection to {Target}.", ex),
        SshException => new IOException(ex.Message, ex),
        _ => null,
    };

    internal void Close()
    {
        IsClosed = true;
        _owner.Forget(this);
        Dispose(_client);
    }

    private static void Dispose(SftpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // closing a dead connection may throw; nothing to do about it
        }
    }

    private static string SafeWorkingDirectory(SftpClient client)
    {
        try
        {
            return string.IsNullOrEmpty(client.WorkingDirectory) ? "/" : client.WorkingDirectory;
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException)
        {
            return "/";
        }
    }
}
