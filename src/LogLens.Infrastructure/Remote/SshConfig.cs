using System.Globalization;
using LogLens.Core.Remote;
using LogLens.Core.Streaming;

namespace LogLens.Infrastructure.Remote;

/// <summary>
/// Reads the hosts defined in an OpenSSH client config (<c>~/.ssh/config</c>) so they can be listed without typing them again. Only
/// <c>Host</c> blocks and the <c>HostName</c>, <c>User</c>, <c>Port</c> and <c>IdentityFile</c> options are used; as in OpenSSH, the first
/// value found for an option wins, and blocks whose patterns match the alias contribute in file order. <c>Match</c> blocks and
/// <c>Include</c> are not followed. Wildcard-only entries (<c>Host *</c>) supply defaults but are not listed themselves.
/// </summary>
public static class SshConfig
{
    /// <summary>The config file's usual place.</summary>
    public static string DefaultPath(string homeFolder) => Path.Combine(homeFolder, ".ssh", "config");

    /// <summary>The hosts in <paramref name="path"/>, or none when the file is missing or unreadable.</summary>
    public static IReadOnlyList<RemoteHost> ReadHosts(string path, string homeFolder, string defaultUser)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024)
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), homeFolder, defaultUser);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Block(string[] patterns)
    {
        public string[] Patterns { get; } = patterns;

        public List<(string Key, string Value)> Options { get; } = [];
    }

    /// <summary>The hosts in config text. <paramref name="defaultUser"/> is used when a host has no <c>User</c> (OpenSSH uses the local user name).</summary>
    public static IReadOnlyList<RemoteHost> Parse(string text, string homeFolder, string defaultUser)
    {
        var blocks = new List<Block> { new(["*"]) }; // options before the first Host line apply to every host
        var aliases = new List<string>();
        var inMatch = false;

        foreach (var raw in text.Split('\n'))
        {
            var (key, value) = SplitLine(raw);
            if (key is null)
            {
                continue;
            }

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                inMatch = false;
                var patterns = Tokens(value);
                blocks.Add(new Block(patterns));
                foreach (var p in patterns)
                {
                    if (p.IndexOfAny(['*', '?', '!']) < 0 && !aliases.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        aliases.Add(p);
                    }
                }
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                inMatch = true; // conditions aren't evaluated: ignore the block
            }
            else if (!inMatch)
            {
                blocks[^1].Options.Add((key, value));
            }
        }

        var hosts = new List<RemoteHost>();
        foreach (var alias in aliases)
        {
            string? hostName = null, user = null, port = null, identity = null;
            foreach (var block in blocks.Where(b => Matches(b.Patterns, alias)))
            {
                foreach (var (key, value) in block.Options)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "hostname":
                            hostName ??= value;
                            break;
                        case "user":
                            user ??= value;
                            break;
                        case "port":
                            port ??= value;
                            break;
                        case "identityfile":
                            identity ??= value;
                            break;
                    }
                }
            }

            var host = new RemoteHost
            {
                Name = alias,
                Kind = RemoteHostKind.Ssh,
                Address = (hostName ?? alias).Replace("%h", alias, StringComparison.Ordinal),
                Port = int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : RemoteHost.DefaultSshPort,
                UserName = user ?? defaultUser,
                KeyFile = identity is null ? string.Empty : ExpandHome(identity, homeFolder, alias),
            }.Normalised();

            if (host.Validate() is null)
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    private static (string? Key, string Value) SplitLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return (null, string.Empty);
        }

        // "Key value", "Key=value" or "Key = value"
        var end = line.IndexOfAny([' ', '\t', '=']);
        if (end <= 0)
        {
            return (null, string.Empty);
        }

        var key = line[..end];
        var value = line[end..].TrimStart(' ', '\t', '=').TrimStart();
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment].TrimEnd();
        }

        return (key, Unquote(value));
    }

    private static string[] Tokens(string value)
        => value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unquote).ToArray();

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static bool Matches(string[] patterns, string alias)
    {
        var matched = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (RollPattern.Matches(pattern[1..], alias))
                {
                    return false;
                }
            }
            else if (RollPattern.Matches(pattern, alias))
            {
                matched = true;
            }
        }

        return matched;
    }

    private static string ExpandHome(string path, string homeFolder, string alias)
    {
        var expanded = path.Replace("%d", homeFolder, StringComparison.Ordinal).Replace("%h", alias, StringComparison.Ordinal);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith(@"~\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(homeFolder, expanded[2..]);
        }

        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }
}
