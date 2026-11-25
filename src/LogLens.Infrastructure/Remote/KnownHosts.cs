using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LogLens.Core.Remote;

namespace LogLens.Infrastructure.Remote;

/// <summary>What is known about a host key that a server presented.</summary>
public enum HostKeyStatus
{
    /// <summary>The key is listed for this host: connect.</summary>
    Trusted,

    /// <summary>Nothing is known about this host (or not for this kind of key): ask the user.</summary>
    Unknown,

    /// <summary>A different key of the same kind is listed for this host: possibly an attack, refuse.</summary>
    Changed,

    /// <summary>The key is marked <c>@revoked</c>: refuse.</summary>
    Revoked,
}

/// <summary>
/// Host keys in OpenSSH <c>known_hosts</c> format: <c>[@marker] patterns keytype base64 [comment]</c>, where patterns are comma-separated
/// host names (<c>[host]:port</c> for ports other than 22) with <c>*</c>, <c>?</c> and <c>!</c>, or a hashed <c>|1|salt|hash</c> entry.
/// Used read-only for the user's <c>~/.ssh/known_hosts</c>; LogLens never writes that file.
/// </summary>
public sealed class KnownHosts
{
    private readonly List<Entry> _entries = [];

    private sealed record Entry(bool Revoked, string[] Patterns, string KeyType, string Key);

    /// <summary>The name a host is listed under: <c>host</c>, or <c>[host]:port</c> when the port isn't 22.</summary>
    public static string HostToken(string host, int port)
        => port == RemoteHost.DefaultSshPort ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port}";

    /// <summary>Reads <c>known_hosts</c> text; lines that can't be understood (and <c>@cert-authority</c> lines) are skipped.</summary>
    public static KnownHosts Parse(string? text)
    {
        var hosts = new KnownHosts();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var revoked = false;
            var index = 0;
            if (parts[0].StartsWith('@'))
            {
                if (parts[0] == "@revoked")
                {
                    revoked = true;
                }
                else
                {
                    continue; // @cert-authority: host certificates aren't used here
                }

                index = 1;
            }

            if (parts.Length < index + 3)
            {
                continue;
            }

            hosts._entries.Add(new Entry(revoked, parts[index].Split(','), parts[index + 1], parts[index + 2]));
        }

        return hosts;
    }

    /// <summary>Lists <paramref name="keyBase64"/> for <paramref name="hostToken"/> (see <see cref="HostToken"/>).</summary>
    public void Add(string hostToken, string keyType, string keyBase64)
        => _entries.Add(new Entry(false, [hostToken], keyType, keyBase64));

    /// <summary>Compares the key a server presented with what is listed for the host.</summary>
    public HostKeyStatus Check(string host, int port, string keyBase64)
    {
        var token = HostToken(host, port);
        var family = KeyTypeOf(keyBase64);
        var sameKind = false;

        foreach (var entry in _entries)
        {
            if (!Matches(entry.Patterns, token))
            {
                continue;
            }

            var same = string.Equals(entry.Key, keyBase64, StringComparison.Ordinal);
            if (entry.Revoked)
            {
                if (same)
                {
                    return HostKeyStatus.Revoked;
                }

                continue;
            }

            if (same)
            {
                return HostKeyStatus.Trusted;
            }

            if (family is not null && string.Equals(KeyTypeOf(entry.Key), family, StringComparison.Ordinal))
            {
                sameKind = true;
            }
        }

        return sameKind ? HostKeyStatus.Changed : HostKeyStatus.Unknown;
    }

    /// <summary>The algorithm named inside an SSH public key blob (<c>ssh-ed25519</c>, <c>ssh-rsa</c> …), or null if it isn't one.</summary>
    public static string? KeyTypeOf(string keyBase64)
    {
        try
        {
            var blob = Convert.FromBase64String(keyBase64);
            if (blob.Length < 4)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(blob);
            return length is > 0 and < 64 && blob.Length >= 4 + length ? Encoding.ASCII.GetString(blob, 4, length) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The OpenSSH-style fingerprint of a key: <c>SHA256:</c> and the unpadded base64 of its SHA-256 hash.</summary>
    public static string Fingerprint(string keyBase64)
    {
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(keyBase64))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return "(not a valid key)";
        }
    }

    private static bool Matches(string[] patterns, string token)
    {
        var matched = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith("|1|", StringComparison.Ordinal))
            {
                matched |= HashedMatches(pattern, token);
            }
            else if (pattern.StartsWith('!'))
            {
                if (Core.Streaming.RollPattern.Matches(pattern[1..], token))
                {
                    return false; // a negated match rules the entry out
                }
            }
            else if (Core.Streaming.RollPattern.Matches(pattern, token))
            {
                matched = true;
            }
        }

        return matched;
    }

    /// <summary><c>|1|base64(salt)|base64(HMAC-SHA1(salt, host))</c>, as written with <c>HashKnownHosts yes</c>.</summary>
    private static bool HashedMatches(string pattern, string token)
    {
        var parts = pattern.Split('|');
        if (parts.Length != 4)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(token));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
