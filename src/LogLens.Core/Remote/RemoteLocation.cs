using System.Globalization;

namespace LogLens.Core.Remote;

/// <summary>
/// A file on an SSH host, written as <c>sftp://user@host[:port]/absolute/path</c>. That string is the file's identity everywhere a local
/// path would be used (tabs, recent files, the session, bookmarks, an event's source file), so it must stay stable: the path is kept as
/// it is (not percent-encoded) and the port is left out when it is 22.
/// </summary>
public readonly record struct RemoteLocation(string UserName, string Host, int Port, string Path)
{
    public const string Scheme = "sftp://";

    /// <summary>True for an <c>sftp://</c> location (which is never a local path).</summary>
    public static bool IsRemote(string? path) => path is not null && path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>A local path made absolute, or a remote location unchanged.</summary>
    /// <exception cref="ArgumentException">A local path that isn't valid.</exception>
    public static string Normalise(string path)
        => IsRemote(path) ? path : System.IO.Path.GetFullPath(path);

    /// <summary>Reads <c>sftp://user@host[:port]/path</c>. The user name and an absolute path are required.</summary>
    public static bool TryParse(string? text, out RemoteLocation location)
    {
        location = default;
        if (!IsRemote(text))
        {
            return false;
        }

        var rest = text!.AsSpan(Scheme.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var authority = rest[..slash];
        var path = rest[slash..].ToString();
        var at = authority.LastIndexOf('@');
        if (at <= 0)
        {
            return false;
        }

        var user = authority[..at].ToString();
        var hostPort = authority[(at + 1)..];
        string host;
        var port = RemoteHost.DefaultSshPort;

        if (hostPort.StartsWith("["))
        {
            // [IPv6 address] or [address]:port
            var close = hostPort.IndexOf(']');
            if (close < 2)
            {
                return false;
            }

            host = hostPort[1..close].ToString();
            var after = hostPort[(close + 1)..];
            if (after.Length > 0 && (after[0] != ':' || !TryPort(after[1..], out port)))
            {
                return false;
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!TryPort(hostPort[(colon + 1)..], out port))
                {
                    return false;
                }

                hostPort = hostPort[..colon];
            }

            host = hostPort.ToString();
        }

        if (host.Length == 0 || user.Length == 0)
        {
            return false;
        }

        location = new RemoteLocation(user, host, port, path);
        return true;
    }

    private static bool TryPort(ReadOnlySpan<char> text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary><c>user@host</c>, with <c>:port</c> when it isn't 22 (an IPv6 address is bracketed).</summary>
    public static string Authority(string userName, string host, int port)
    {
        var h = host.Contains(':') ? $"[{host}]" : host;
        var p = port == RemoteHost.DefaultSshPort ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture);
        return userName.Length > 0 ? $"{userName}@{h}{p}" : h + p;
    }

    /// <summary>Identifies one SSH login: connections are shared by every file with the same key.</summary>
    public string ConnectionKey => ConnectionKeyOf(UserName, Host, Port);

    public static string ConnectionKeyOf(string userName, string host, int port)
        => Authority(userName, host.ToLowerInvariant(), port);

    /// <summary>The same host and login, another path.</summary>
    public RemoteLocation WithPath(string path) => this with { Path = path };

    public override string ToString() => Scheme + Authority(UserName, Host, Port) + Path;

    // ------------------------------------------------------------------ POSIX paths (remote paths never use backslashes)

    /// <summary>The last part of a POSIX path: <c>/var/log/app.txt</c> → <c>app.txt</c>.</summary>
    public static string FileName(string posixPath)
    {
        var trimmed = posixPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The folder above <paramref name="posixPath"/>: <c>/var/log</c> → <c>/var</c>; <c>/</c> stays <c>/</c>.</summary>
    public static string Parent(string posixPath)
    {
        var trimmed = posixPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>Joins a folder and a name: <c>/var/log</c> + <c>app.txt</c> → <c>/var/log/app.txt</c>.</summary>
    public static string Combine(string folder, string name)
        => folder.EndsWith('/') ? folder + name : folder + "/" + name;
}
