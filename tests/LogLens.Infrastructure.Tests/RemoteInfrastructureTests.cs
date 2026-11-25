using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LogLens.Core.Remote;
using LogLens.Core.Settings;
using LogLens.Core.State;
using LogLens.Core.Streaming;
using LogLens.Infrastructure.Instances;
using LogLens.Infrastructure.Loading;
using LogLens.Infrastructure.Remote;
using LogLens.Infrastructure.State;

namespace LogLens.Infrastructure.Tests;


/// <summary>An SSH public key blob (type string + fake key bytes), base64, as it appears in known_hosts.</summary>
internal static class Keys
{
    public static string Make(string type, byte seed)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + 4 + 32];
        BinaryPrimitives.WriteInt32BigEndian(blob, name.Length);
        name.CopyTo(blob, 4);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + name.Length), 32);
        blob.AsSpan(8 + name.Length).Fill(seed);
        return Convert.ToBase64String(blob);
    }

    public static readonly string Ed1 = Make("ssh-ed25519", 1);
    public static readonly string Ed2 = Make("ssh-ed25519", 2);
    public static readonly string Rsa1 = Make("ssh-rsa", 3);
}

public sealed class KnownHostsTests
{
    [Fact]
    public void A_listed_key_is_trusted_and_another_of_the_same_kind_has_changed()
    {
        var known = KnownHosts.Parse($"# comment\nweb01,10.0.0.5 ssh-ed25519 {Keys.Ed1} me@laptop\n");

        Assert.Equal(HostKeyStatus.Trusted, known.Check("web01", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Trusted, known.Check("WEB01", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Trusted, known.Check("10.0.0.5", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Changed, known.Check("web01", 22, Keys.Ed2));
        Assert.Equal(HostKeyStatus.Unknown, known.Check("web01", 22, Keys.Rsa1)); // another kind of key: not a conflict
        Assert.Equal(HostKeyStatus.Unknown, known.Check("web02", 22, Keys.Ed1));
    }

    [Fact]
    public void Other_ports_are_listed_in_brackets()
    {
        var known = KnownHosts.Parse($"[web01]:2222 ssh-ed25519 {Keys.Ed1}");
        Assert.Equal(HostKeyStatus.Trusted, known.Check("web01", 2222, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Unknown, known.Check("web01", 22, Keys.Ed1));
    }

    [Fact]
    public void Hashed_entries_match_by_hmac()
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("web01"));
        var known = KnownHosts.Parse($"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 {Keys.Ed1}");

        Assert.Equal(HostKeyStatus.Trusted, known.Check("web01", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Changed, known.Check("web01", 22, Keys.Ed2));
        Assert.Equal(HostKeyStatus.Unknown, known.Check("web02", 22, Keys.Ed1));
    }

    [Fact]
    public void Wildcards_negation_revocation_and_cert_authorities()
    {
        var known = KnownHosts.Parse(
            $"*.example.com,!bad.example.com ssh-ed25519 {Keys.Ed1}\n" +
            $"@revoked * ssh-ed25519 {Keys.Ed2}\n" +
            $"@cert-authority *.example.com ssh-rsa {Keys.Rsa1}\n" +
            "garbage\n");

        Assert.Equal(HostKeyStatus.Trusted, known.Check("web.example.com", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Unknown, known.Check("bad.example.com", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Revoked, known.Check("web.example.com", 22, Keys.Ed2));
        Assert.Equal(HostKeyStatus.Unknown, known.Check("web.example.com", 22, Keys.Rsa1)); // CA lines aren't host keys
    }

    [Fact]
    public void Key_type_and_fingerprint_come_from_the_blob()
    {
        Assert.Equal("ssh-ed25519", KnownHosts.KeyTypeOf(Keys.Ed1));
        Assert.Null(KnownHosts.KeyTypeOf("not base64!"));
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Keys.Ed1))).TrimEnd('=');
        Assert.Equal(expected, KnownHosts.Fingerprint(Keys.Ed1));
    }
}

public sealed class HostKeyTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loglens-trust-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Accepted_keys_are_remembered_replaced_and_forgotten()
    {
        var store = new InMemoryStateStore();
        var trust = new HostKeyTrust(store, knownHostsPath: Path.Combine(_dir, "missing"));

        Assert.Equal(HostKeyStatus.Unknown, trust.Snapshot().Check("web01", 2222, Keys.Ed1));

        trust.Trust("web01", 2222, Keys.Ed1);
        Assert.Equal(HostKeyStatus.Trusted, trust.Snapshot().Check("web01", 2222, Keys.Ed1));
        Assert.Equal("[web01]:2222", Assert.Single(store.Load().TrustedHostKeys).Host);

        trust.Trust("web01", 2222, Keys.Ed2); // the server was reinstalled and the user accepted its new key
        Assert.Equal(Keys.Ed2, Assert.Single(store.Load().TrustedHostKeys).Key);

        trust.Forget("WEB01", 2222);
        Assert.Empty(store.Load().TrustedHostKeys);
    }

    [Fact]
    public void Reads_the_users_known_hosts_without_changing_it()
    {
        var file = Path.Combine(_dir, "known_hosts");
        var text = $"web01 ssh-ed25519 {Keys.Ed1}\n";
        File.WriteAllText(file, text);
        var trust = new HostKeyTrust(new InMemoryStateStore(), file);

        Assert.Equal(HostKeyStatus.Trusted, trust.Snapshot().Check("web01", 22, Keys.Ed1));
        Assert.Equal(HostKeyStatus.Changed, trust.Snapshot().Check("web01", 22, Keys.Ed2));

        trust.Trust("web02", 22, Keys.Ed2);
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void Trusted_keys_survive_a_save_and_load()
    {
        var paths = new AppPaths(_dir, _dir);
        new JsonStateStore(paths).Save(new AppState { TrustedHostKeys = [new TrustedHostKey { Host = "web01", KeyType = "ssh-ed25519", Key = Keys.Ed1 }, new TrustedHostKey()] });

        var loaded = new JsonStateStore(paths).Load();
        Assert.Equal(Keys.Ed1, Assert.Single(loaded.TrustedHostKeys).Key); // the empty one is dropped
    }
}

public sealed class SshConfigTests
{
    private const string Home = @"C:\Users\me";

    [Fact]
    public void Lists_concrete_hosts_with_their_options_and_defaults()
    {
        const string config = """
            # global defaults
            User fallback
            Host web01
                HostName web01.example.com
                User deploy
                Port 2222
                IdentityFile ~/.ssh/web_key
            Host db db-alias
              HostName=10.0.0.9
            Host "quoted"
                HostName %h.internal
            Host *.example.com
                User wild
            Host *
                IdentityFile ~/.ssh/id_default
                User ignored-because-global-came-first
            Match host web01
                User from-match
            """;

        var hosts = SshConfig.Parse(config, Home, "localuser");

        Assert.Equal(["web01", "db", "db-alias", "quoted"], hosts.Select(h => h.Name));
        var web = hosts[0];
        Assert.Equal("web01.example.com", web.Address);
        Assert.Equal("fallback", web.UserName); // options before the first Host apply first, as in OpenSSH
        Assert.Equal(2222, web.Port);
        Assert.Equal(@"C:\Users\me\.ssh\web_key", web.KeyFile);

        var db = hosts[1];
        Assert.Equal("10.0.0.9", db.Address);
        Assert.Equal(22, db.Port);
        Assert.Equal(@"C:\Users\me\.ssh\id_default", db.KeyFile);

        Assert.Equal("quoted.internal", hosts[3].Address);
    }

    [Fact]
    public void Without_a_user_the_local_user_name_is_used()
    {
        var host = Assert.Single(SshConfig.Parse("Host box\n  HostName 192.168.1.20\n", Home, "localuser"));
        Assert.Equal("localuser", host.UserName);
        Assert.Equal(RemoteHostKind.Ssh, host.Kind);
    }

    [Fact]
    public void A_missing_file_lists_nothing() => Assert.Empty(SshConfig.ReadHosts(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Home, "me"));
}

public sealed class RemoteSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loglens-remote-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppPaths Paths => new(_dir, _dir);

    [Fact]
    public void Saved_hosts_round_trip_and_unusable_ones_are_dropped()
    {
        var path = Paths.SettingsFile;
        var store = new JsonSettingsStore(Paths);
        store.Save(new AppSettings
        {
            RemoteHosts =
            [
                new RemoteHost { Name = "Web", Address = "web01", UserName = "deploy", Port = 2222, KeyFile = @"C:\k\id", StartFolder = "/var/log" },
                new RemoteHost { Kind = RemoteHostKind.WindowsShare, Address = @"\\srv\logs" },
                new RemoteHost { Address = "no-user" },
            ],
        });

        var loaded = store.Load().RemoteHosts;
        Assert.Equal(2, loaded.Count);
        Assert.Equal(new RemoteHost { Name = "Web", Address = "web01", UserName = "deploy", Port = 2222, KeyFile = @"C:\k\id", StartFolder = "/var/log" }, loaded[0]);
        Assert.Equal(@"\\srv\logs", loaded[1].Name);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("displayName", json, StringComparison.Ordinal); // computed, not stored
        Assert.DoesNotContain("summary", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Hand_written_hosts_are_read()
    {
        var path = Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "version": 1, "remoteHosts": [
                { "name": "Web", "kind": "ssh", "address": "web01", "port": 2222, "userName": "deploy" },
                { "name": "Share", "kind": "windowsShare", "address": "\\\\buildsrv\\logs" } ] }
            """);

        var hosts = new JsonSettingsStore(Paths).Load().RemoteHosts;
        Assert.Equal(["Web", "Share"], hosts.Select(h => h.Name));
        Assert.Equal(@"\\buildsrv\logs", hosts[1].Address);
        Assert.Equal(RemoteHostKind.WindowsShare, hosts[1].Kind);
    }

    [Fact]
    public void Exported_settings_carry_the_hosts()
    {
        var file = Path.Combine(_dir, "export.json");
        var settings = new AppSettings { RemoteHosts = [new RemoteHost { Address = "web01", UserName = "deploy" }] };
        JsonSettingsStore.Export(settings, file);

        var imported = JsonSettingsStore.Import(file, new AppSettings());
        Assert.Equal("web01", Assert.Single(imported.RemoteHosts).Address);
    }

    [Fact]
    public void A_forwarded_launch_keeps_remote_locations()
    {
        var request = new LaunchRequest(@"C:\work", ["sftp://deploy@web01/var/log/a.txt", "local.txt"]);
        Assert.Equal(["sftp://deploy@web01/var/log/a.txt", @"C:\work\local.txt"], request.ResolvePaths());
    }
}

/// <summary>
/// A remote file system in memory: files can grow, roll over and "lose the connection", so the reader's handling of remote files can be
/// tested without an SSH server.
/// </summary>
internal sealed class FakeRemoteFileSystem : ILogFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Data, DateTime Written)> _files = new(StringComparer.Ordinal);
    private int _failNext;

    public bool SupportsWatching => false;

    public bool Offline { get; set; }

    public int Opens { get; private set; }

    /// <summary>Fails the next N reads (not opens) of a file, like a connection that drops mid-read.</summary>
    public void FailReads(int count)
    {
        lock (_gate)
        {
            _failNext = count;
        }
    }

    public void Write(string path, string text, bool append = true)
    {
        lock (_gate)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var data = append && _files.TryGetValue(path, out var existing) ? [.. existing.Data, .. bytes] : bytes;
            _files[path] = (data, DateTime.UtcNow);
        }
    }

    public Stream OpenRead(string path)
    {
        lock (_gate)
        {
            if (Offline)
            {
                throw new RemoteUnavailableException("Lost the connection to the fake host.");
            }

            Opens++;
            if (!_files.TryGetValue(path, out var file))
            {
                throw new FileNotFoundException("not found", path);
            }

            return new FlakyStream(file.Data, this);
        }
    }

    private bool ShouldFail()
    {
        lock (_gate)
        {
            if (_failNext <= 0)
            {
                return false;
            }

            _failNext--;
            return true;
        }
    }

    public DateTime GetCreatedUtc(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path, out var f) ? f.Written : throw new FileNotFoundException("not found", path);
        }
    }

    public IEnumerable<RollCandidate> ListFiles(string folder)
    {
        lock (_gate)
        {
            return _files.Where(f => GetFolder(f.Key) == folder).Select(f => new RollCandidate(RemoteLocation.FileName(f.Key), f.Value.Written)).ToList();
        }
    }

    public string? GetFolder(string path)
    {
        RemoteLocation.TryParse(path, out var l);
        return l.WithPath(RemoteLocation.Parent(l.Path)).ToString();
    }

    public string Combine(string folder, string name)
    {
        RemoteLocation.TryParse(folder, out var l);
        return l.WithPath(RemoteLocation.Combine(l.Path, name)).ToString();
    }

    private sealed class FlakyStream(byte[] data, FakeRemoteFileSystem owner) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => owner.ShouldFail() ? throw new RemoteUnavailableException("Lost the connection mid-read.") : base.Read(buffer, offset, count);
    }
}

public sealed class RemoteStreamTests
{
    private const string File1 = "sftp://deploy@web01/var/log/app/log20261120.txt";

    private static string Line(int i) => $"2026-11-20 10:00:{i % 60:00}.000 +00:00 [INF] Event {i}\n";

    private static StreamOptions Options() => StreamHarness.Fast() with { UseWatcher = false, ReconnectDelay = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public async Task Reads_and_tails_a_remote_file()
    {
        var fs = new FakeRemoteFileSystem();
        fs.Write(File1, Line(1) + Line(2));
        await using var h = new StreamHarness(File1, Options(), fs).Start();
        h.Stream.Follow = true;
        await h.WaitForLoaded();
        Assert.Equal("log20261120.txt", h.Read(() => h.Doc.FileName));

        fs.Write(File1, Line(3));
        await h.WaitFor(() => h.Doc.Events.Count == 3, "the appended event");
        Assert.Equal(["Event 1", "Event 2", "Event 3"], h.Messages());
    }

    [Fact]
    public async Task A_lost_connection_shows_reconnecting_and_resumes_without_losing_or_repeating_events()
    {
        var fs = new FakeRemoteFileSystem();
        fs.Write(File1, Line(1));
        await using var h = new StreamHarness(File1, Options(), fs).Start();
        h.Stream.Follow = true;
        await h.WaitForLoaded();

        fs.Offline = true;
        fs.Write(File1, Line(2));
        await h.WaitFor(() => h.Batches.OfType<StatusBatch>().Any(s => s.State == StreamState.Reconnecting), "the reconnecting status");

        fs.Offline = false;
        await h.WaitFor(() => h.Doc.Events.Count == 2, "the event written while offline");
        await h.WaitFor(() => h.Batches.OfType<StatusBatch>().Last().State == StreamState.Following, "following again");

        fs.FailReads(1); // the connection drops in the middle of the next read
        fs.Write(File1, Line(3));
        await h.WaitFor(() => h.Doc.Events.Count == 3, "the event after a failed read");
        Assert.Equal(["Event 1", "Event 2", "Event 3"], h.Messages());
        Assert.Null(h.Error);
    }

    [Fact]
    public async Task Follows_a_remote_log_to_its_next_file()
    {
        var fs = new FakeRemoteFileSystem();
        fs.Write(File1, Line(1));
        await using var h = new StreamHarness(File1, Options(), fs).Start();
        h.Stream.Follow = true;
        await h.WaitForLoaded();

        fs.Write("sftp://deploy@web01/var/log/app/log20261121.txt", Line(2));
        fs.Write("sftp://deploy@web01/var/log/other.txt", Line(9));
        await h.WaitFor(() => h.Doc.Events.Count == 2, "the next day's event");
        Assert.Equal("sftp://deploy@web01/var/log/app/log20261121.txt", h.Read(() => h.Doc.CurrentFilePath));
    }

    [Fact]
    public async Task A_missing_remote_file_fails_like_a_local_one()
    {
        var fs = new FakeRemoteFileSystem();
        await using var h = new StreamHarness(File1, Options(), fs).Start();
        await h.WaitForError();
        Assert.IsType<FileNotFoundException>(h.Error);
    }

    [Fact]
    public async Task Loading_and_merging_read_through_the_file_system()
    {
        var fs = new FakeRemoteFileSystem();
        fs.Write(File1, Line(1) + Line(3));
        fs.Write("sftp://deploy@web02/var/log/b.txt", Line(2));

        await using var merged = new MergedLogStream([File1, "sftp://deploy@web02/var/log/b.txt"], fileSystemFor: _ => fs);
        var events = new List<LogLens.Core.Models.LogEvent>();
        await foreach (var batch in merged.ReadAsync(CancellationToken.None))
        {
            events.AddRange(((ChunkBatch)batch).Delta.Events);
        }

        Assert.Equal(["Event 1", "Event 2", "Event 3"], events.Select(e => e.Message));
        Assert.Equal(File1, events[0].SourceFile);
    }

    [Fact]
    public async Task A_batch_load_fails_instead_of_waiting_for_an_offline_host()
    {
        var fs = new FakeRemoteFileSystem { Offline = true };
        await Assert.ThrowsAsync<RemoteUnavailableException>(() => new LogFileLoader().LoadAsync(File1, fileSystem: fs));
    }
}

public sealed class RemoteOpeningTests
{
    private const string File1 = "sftp://deploy@web01:2222/var/log/app.txt";

    [Fact]
    public async Task The_factory_reads_an_sftp_file_through_its_hosts_file_system()
    {
        var fs = new FakeRemoteFileSystem();
        fs.Write(File1, "2026-11-20 10:00:00.000 +00:00 [INF] Remote event\n");
        var factory = new FileLogStreamFactory(StreamHarness.Fast(), remoteFileSystem: path => path == File1 ? fs : null);

        await using var stream = factory.Open(File1, LogLens.Core.Formats.LogFormats.SerilogText);
        var chunk = await FirstChunkAsync(stream);

        Assert.Equal("Remote event", Assert.Single(chunk.Delta.Events).Message);
        Assert.Equal(File1, chunk.FilePath);
    }

    [Fact]
    public void Opening_an_sftp_file_whose_host_is_not_connected_says_so()
    {
        var factory = new FileLogStreamFactory(remoteFileSystem: _ => null);

        Assert.Contains("Not connected", Assert.Throws<RemoteUnavailableException>(() => factory.Open(File1)).Message);
        Assert.Throws<RemoteUnavailableException>(() => new FileLogStreamFactory().Open(File1));
    }

    [Fact]
    public void Remote_files_are_polled_and_their_folder_listed_rarely()
    {
        var options = new StreamOptions().ForRemote();

        Assert.False(options.UseWatcher);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RollCheckInterval);
    }

    [Fact]
    public void The_command_line_keeps_sftp_locations_as_they_are()
        => Assert.Equal([File1, @"C:\work\a.log"], CommandLine.Parse([File1, "a.log"], @"C:\work").Files);

    [Fact]
    public async Task The_connector_needs_nothing_for_local_files_and_rejects_bad_locations()
    {
        using var connections = new SftpConnections(new HostKeyTrust(new InMemoryStateStore(), null), Path.GetTempPath());
        var connector = new RemoteConnector(connections, () => [], new NoPrompt());

        Assert.True((await connector.ConnectAsync(@"C:\logs\a.log", CancellationToken.None)).Connected);
        var bad = await connector.ConnectAsync("sftp://no-user-or-path", CancellationToken.None);
        Assert.False(bad.Connected);
        Assert.False(bad.Cancelled);
        Assert.Contains("not a valid", bad.Problem);
    }

    [Fact]
    public void The_connector_uses_the_saved_host_that_serves_a_file()
    {
        using var connections = new SftpConnections(new HostKeyTrust(new InMemoryStateStore(), null), Path.GetTempPath());
        var saved = new RemoteHost { Name = "Web", Address = "web01", Port = 2222, UserName = "deploy", KeyFile = @"C:\k\web" };
        var other = new RemoteHost { Name = "Other", Address = "web01", Port = 22, UserName = "deploy" };
        var connector = new RemoteConnector(connections, () => [other, saved], new NoPrompt());

        RemoteLocation.TryParse(File1, out var location);
        Assert.Same(saved, connector.HostFor(location));

        RemoteLocation.TryParse("sftp://ops@db01/var/log/x.txt", out var unknown);
        var made = connector.HostFor(unknown);
        Assert.Equal(("db01", 22, "ops", ""), (made.Address, made.Port, made.UserName, made.KeyFile));
    }

    private static async Task<ChunkBatch> FirstChunkAsync(ILogStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var batch in stream.ReadAsync(cts.Token))
        {
            if (batch is ChunkBatch chunk)
            {
                return chunk;
            }
        }

        throw new InvalidOperationException("No chunk arrived.");
    }

    private sealed class NoPrompt : ISshPrompt
    {
        public Task<string?> AskPasswordAsync(SshTarget target, string? problem, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskPassphraseAsync(SshTarget target, string keyFile, string? problem, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmHostKeyAsync(SshTarget target, HostKeyInfo key, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
