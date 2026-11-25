namespace LogLens.Core.Remote;

/// <summary>What became of connecting to a remote file's host.</summary>
/// <param name="Connected">The file can be read now.</param>
/// <param name="Cancelled">The user cancelled a question (password, passphrase, host key).</param>
/// <param name="Problem">Why connecting failed, for the user; null when connected or cancelled.</param>
public sealed record RemoteConnectResult(bool Connected, bool Cancelled = false, string? Problem = null)
{
    public static RemoteConnectResult Success { get; } = new(true);
}

/// <summary>
/// Gets a remote file's host ready before the file is opened (FR-O7): connects to the SSH host of an <c>sftp://</c> path, which may ask the user
/// for a password, a key passphrase or about an unknown host key. Called on the UI thread.
/// </summary>
public interface IRemoteConnector
{
    /// <summary>Connects to the host of <paramref name="path"/> unless it is connected already. Local paths need nothing.</summary>
    Task<RemoteConnectResult> ConnectAsync(string path, CancellationToken cancellationToken);
}
