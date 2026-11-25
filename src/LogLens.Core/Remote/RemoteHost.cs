using System.Text.Json.Serialization;

namespace LogLens.Core.Remote;

/// <summary>How LogLens reaches a remote host's files.</summary>
public enum RemoteHostKind
{
    /// <summary>An SSH server; files are read over SFTP.</summary>
    Ssh = 0,

    /// <summary>A Windows file share (<c>\\server\share\folder</c>); Windows does the network access.</summary>
    WindowsShare = 1,
}

/// <summary>
/// A remote host saved in File ▸ Open from remote host (FR-O7). Holds no secrets: passwords and key passphrases are asked for when
/// connecting and kept in memory only.
/// </summary>
public sealed record RemoteHost
{
    public const int DefaultSshPort = 22;

    /// <summary>What the host is called in the list; defaults to <see cref="Address"/>.</summary>
    public string Name { get; init; } = string.Empty;

    public RemoteHostKind Kind { get; init; } = RemoteHostKind.Ssh;

    /// <summary>SSH: the host name or IP address. Windows share: the folder, <c>\\server\share\logs</c>.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>SSH port.</summary>
    public int Port { get; init; } = DefaultSshPort;

    /// <summary>SSH user name.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>SSH private key file (OpenSSH, PEM or PuTTY .ppk); empty to try the usual keys in <c>%USERPROFILE%\.ssh</c>.</summary>
    public string KeyFile { get; init; } = string.Empty;

    /// <summary>SSH: the folder to show first (absolute, or empty for the user's home folder).</summary>
    public string StartFolder { get; init; } = string.Empty;

    /// <summary>The name shown in lists.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    /// <summary>A one-line description of where the host is: <c>deploy@web01:2222</c> or the share's path.</summary>
    [JsonIgnore]
    public string Summary => Kind == RemoteHostKind.WindowsShare
        ? Address
        : RemoteLocation.Authority(UserName, Address, Port);

    /// <summary>What is wrong with the host's fields, or null when it can be saved.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            return Kind == RemoteHostKind.WindowsShare ? "Enter the share's folder, for example \\\\server\\share\\logs." : "Enter the host name or IP address.";
        }

        if (Kind == RemoteHostKind.WindowsShare)
        {
            return Address.StartsWith(@"\\", StringComparison.Ordinal) && Address.TrimStart('\\').Contains('\\')
                ? null
                : "A share's folder starts with \\\\server\\share.";
        }

        if (Address.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
        {
            return "The host name can't contain spaces, slashes or @. Put the user name in its own field.";
        }

        if (Port is < 1 or > 65535)
        {
            return "The port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            return "Enter the user name to sign in with.";
        }

        if (UserName.Any(c => char.IsWhiteSpace(c) || c is '@' or '/' or ':'))
        {
            return "The user name can't contain spaces, @, / or :.";
        }

        if (StartFolder.Length > 0 && !StartFolder.StartsWith('/') && !StartFolder.StartsWith('~'))
        {
            return "The start folder must be an absolute path such as /var/log, or empty for the home folder.";
        }

        return null;
    }

    /// <summary>The same host with surrounding spaces removed and an empty name filled in.</summary>
    public RemoteHost Normalised()
    {
        var address = (Address ?? string.Empty).Trim();
        if (Kind == RemoteHostKind.WindowsShare)
        {
            address = address.TrimEnd('\\');
        }

        var name = (Name ?? string.Empty).Trim();
        return this with
        {
            Name = name.Length > 0 ? name : address,
            Address = address,
            UserName = (UserName ?? string.Empty).Trim(),
            KeyFile = (KeyFile ?? string.Empty).Trim().Trim('"'),
            StartFolder = (StartFolder ?? string.Empty).Trim(),
        };
    }

    /// <summary>
    /// True when every word of <paramref name="filter"/> is found (ignoring case) in the host's name, address, user, port or
    /// summary; an empty filter matches every host.
    /// </summary>
    public bool Matches(string? filter)
    {
        var words = (filter ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return true;
        }

        string[] fields = Kind == RemoteHostKind.WindowsShare
            ? [DisplayName, Address]
            : [DisplayName, Address, UserName, Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Summary];
        return words.All(word => fields.Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A copy of this host to save as a new one, named "<c>name (copy)</c>" — or "<c>(copy 2)</c>" and so on — so the name
    /// isn't one of <paramref name="takenNames"/> (compared ignoring case).
    /// </summary>
    public RemoteHost Duplicate(IEnumerable<string> takenNames)
    {
        var taken = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        var name = DisplayName;
        var copy = $"{name} (copy)";
        for (var n = 2; taken.Contains(copy); n++)
        {
            copy = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} (copy {n})");
        }

        return this with { Name = copy };
    }

    /// <summary>True when <paramref name="location"/> is a file on this (SSH) host.</summary>
    public bool Serves(RemoteLocation location)
        => Kind == RemoteHostKind.Ssh
           && string.Equals(Address, location.Host, StringComparison.OrdinalIgnoreCase)
           && string.Equals(UserName, location.UserName, StringComparison.Ordinal)
           && Port == location.Port;
}
