using LogLens.Core.State;

namespace LogLens.Infrastructure.Remote;

/// <summary>Decides whether an SSH server's host key is the one expected for that host.</summary>
public interface IHostKeyTrust
{
    /// <summary>
    /// Everything currently trusted, as an in-memory list. Connecting checks the server's key against this snapshot (on the connection's
    /// worker thread) instead of reading files there.
    /// </summary>
    KnownHosts Snapshot();

    /// <summary>Remembers the key the user accepted for this host (replacing a previously accepted key of the same kind).</summary>
    void Trust(string host, int port, string keyBase64);

    /// <summary>Forgets every key the user accepted in LogLens for this host. Entries in <c>~/.ssh/known_hosts</c> are not touched.</summary>
    void Forget(string host, int port);
}

/// <summary>
/// Host keys from the user's <c>~/.ssh/known_hosts</c> (read on every check, never written) plus the keys accepted in LogLens, which
/// are kept in <c>state.json</c> (<see cref="AppState.TrustedHostKeys"/>). Call only from the UI thread, like the other users of
/// <see cref="IStateStore"/>: a load racing a save on another thread could see a half-replaced file.
/// </summary>
public sealed class HostKeyTrust(IStateStore store, string? knownHostsPath, Action<Exception>? onSaveError = null) : IHostKeyTrust
{
    public KnownHosts Snapshot()
    {
        var known = KnownHosts.Parse(ReadKnownHosts());
        foreach (var k in store.Load().TrustedHostKeys)
        {
            known.Add(k.Host, k.KeyType, k.Key);
        }

        return known;
    }

    public void Trust(string host, int port, string keyBase64)
    {
        var token = KnownHosts.HostToken(host, port);
        var type = KnownHosts.KeyTypeOf(keyBase64) ?? string.Empty;
        var state = store.Load();
        var keys = state.TrustedHostKeys
            .Where(k => !(string.Equals(k.Host, token, StringComparison.OrdinalIgnoreCase) && string.Equals(k.KeyType, type, StringComparison.Ordinal)))
            .Append(new TrustedHostKey { Host = token, KeyType = type, Key = keyBase64 })
            .ToList();
        Save(state with { TrustedHostKeys = keys });
    }

    public void Forget(string host, int port)
    {
        var token = KnownHosts.HostToken(host, port);
        var state = store.Load();
        var keys = state.TrustedHostKeys.Where(k => !string.Equals(k.Host, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count != state.TrustedHostKeys.Count)
        {
            Save(state with { TrustedHostKeys = keys });
        }
    }

    private void Save(AppState state)
    {
        try
        {
            store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSaveError?.Invoke(ex);
        }
    }

    private string? ReadKnownHosts()
    {
        if (string.IsNullOrEmpty(knownHostsPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(knownHostsPath);
            if (!info.Exists || info.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            using var stream = new FileStream(knownHostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
