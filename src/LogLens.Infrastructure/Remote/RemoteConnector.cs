using LogLens.Core.Remote;
using Serilog;

namespace LogLens.Infrastructure.Remote;

/// <summary>
/// Connects to the SSH host of an <c>sftp://</c> file before it is opened from anywhere but the remote-host dialog — recent files, the restored
/// session, the command line (FR-O7). The host's settings (its key file) come from the saved hosts, then from <c>~/.ssh/config</c>; a host known
/// from neither is tried with the user and port in the path and the usual keys.
/// </summary>
/// <param name="connections">The shared SSH connections.</param>
/// <param name="hosts">The saved hosts followed by the ones in <c>~/.ssh/config</c>, read when needed.</param>
/// <param name="prompt">Asks for passwords, passphrases and about unknown host keys.</param>
/// <param name="log">The diagnostics log.</param>
public sealed class RemoteConnector(SftpConnections connections, Func<IEnumerable<RemoteHost>> hosts, ISshPrompt prompt, ILogger? log = null) : IRemoteConnector
{
    public async Task<RemoteConnectResult> ConnectAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!RemoteLocation.IsRemote(path) || connections.FileSystemFor(path) is not null)
        {
            return RemoteConnectResult.Success;
        }

        if (!RemoteLocation.TryParse(path, out var location))
        {
            return new RemoteConnectResult(false, Problem: $"{path} is not a valid sftp://user@host/path location.");
        }

        var host = HostFor(location);
        try
        {
            await connections.ConnectAsync(SshTarget.From(host), prompt, cancellationToken);
            return RemoteConnectResult.Success;
        }
        catch (OperationCanceledException)
        {
            return new RemoteConnectResult(false, Cancelled: true);
        }
        catch (IOException ex)
        {
            log?.Warning(ex, "Could not connect to {Host}", host.Summary);
            return new RemoteConnectResult(false, Problem: ex.Message);
        }
    }

    /// <summary>The saved (or <c>~/.ssh/config</c>) host that serves <paramref name="location"/>, else one made from the location itself.</summary>
    public RemoteHost HostFor(RemoteLocation location)
        => hosts().FirstOrDefault(h => h.Serves(location))
           ?? new RemoteHost { Address = location.Host, Port = location.Port, UserName = location.UserName };
}
