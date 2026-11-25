using LogLens.Core.Remote;
using LogLens.Core.Settings;
using LogLens.Core.State;

namespace LogLens.Core.Tests;

public sealed class RemoteLocationTests
{
    [Theory]
    [InlineData("sftp://deploy@web01/var/log/app.txt", "deploy", "web01", 22, "/var/log/app.txt")]
    [InlineData("sftp://deploy@web01:2222/var/log/app.txt", "deploy", "web01", 2222, "/var/log/app.txt")]
    [InlineData("SFTP://ubuntu@10.0.0.5/home/ubuntu/logs/log 2026.txt", "ubuntu", "10.0.0.5", 22, "/home/ubuntu/logs/log 2026.txt")]
    [InlineData("sftp://root@[fe80::1]:22/x", "root", "fe80::1", 22, "/x")]
    [InlineData("sftp://root@[::1]/x", "root", "::1", 22, "/x")]
    public void Parses_user_host_port_and_path(string text, string user, string host, int port, string path)
    {
        Assert.True(RemoteLocation.TryParse(text, out var location));
        Assert.Equal(new RemoteLocation(user, host, port, path), location);
    }

    [Theory]
    [InlineData("C:\\logs\\app.txt")]
    [InlineData("sftp://web01/var/log/app.txt")] // no user
    [InlineData("sftp://deploy@web01")] // no path
    [InlineData("sftp://deploy@web01:0/x")]
    [InlineData("sftp://deploy@web01:70000/x")]
    [InlineData("sftp://deploy@web01:abc/x")]
    [InlineData("sftp://@web01/x")]
    [InlineData("sftp://deploy@/x")]
    [InlineData("sftp://deploy@[::1/x")]
    [InlineData(null)]
    public void Rejects_what_is_not_a_complete_location(string? text) => Assert.False(RemoteLocation.TryParse(text, out _));

    [Fact]
    public void Formats_back_to_the_same_text_leaving_out_port_22()
    {
        Assert.Equal("sftp://deploy@web01/var/log/a.txt", new RemoteLocation("deploy", "web01", 22, "/var/log/a.txt").ToString());
        Assert.Equal("sftp://deploy@web01:2222/a.txt", new RemoteLocation("deploy", "web01", 2222, "/a.txt").ToString());
        Assert.Equal("sftp://root@[::1]:2200/a", new RemoteLocation("root", "::1", 2200, "/a").ToString());

        foreach (var text in new[] { "sftp://deploy@web01/var/log/a.txt", "sftp://deploy@web01:2222/a b/c.txt", "sftp://root@[::1]:2200/a" })
        {
            Assert.True(RemoteLocation.TryParse(text, out var location));
            Assert.Equal(text, location.ToString());
        }
    }

    [Fact]
    public void Connection_key_ignores_host_case_and_path()
    {
        RemoteLocation.TryParse("sftp://deploy@WEB01/a", out var a);
        RemoteLocation.TryParse("sftp://deploy@web01/b/c", out var b);
        Assert.Equal(a.ConnectionKey, b.ConnectionKey);
        Assert.NotEqual(a.ConnectionKey, new RemoteLocation("other", "web01", 22, "/a").ConnectionKey);
        Assert.NotEqual(a.ConnectionKey, new RemoteLocation("deploy", "web01", 2222, "/a").ConnectionKey);
    }

    [Theory]
    [InlineData("/var/log/app.txt", "/var/log", "app.txt")]
    [InlineData("/var/log/", "/var", "log")]
    [InlineData("/var", "/", "var")]
    [InlineData("/", "/", "")]
    public void Posix_path_parts(string path, string parent, string name)
    {
        Assert.Equal(parent, RemoteLocation.Parent(path));
        Assert.Equal(name, RemoteLocation.FileName(path));
    }

    [Fact]
    public void Combine_joins_with_one_slash()
    {
        Assert.Equal("/var/log/a.txt", RemoteLocation.Combine("/var/log", "a.txt"));
        Assert.Equal("/a.txt", RemoteLocation.Combine("/", "a.txt"));
    }

    [Fact]
    public void Normalise_keeps_remote_locations_and_makes_local_paths_absolute()
    {
        Assert.Equal("sftp://u@h/x/../y.txt", RemoteLocation.Normalise("sftp://u@h/x/../y.txt"));
        Assert.Equal(Path.GetFullPath("rel.txt"), RemoteLocation.Normalise("rel.txt"));
    }

    [Fact]
    public void Recent_files_keep_remote_locations_as_they_are()
    {
        var recent = new RecentFilesService(new InMemoryStateStore());
        recent.Add("sftp://deploy@web01/var/log/a.txt");
        recent.Add("sftp://deploy@WEB01/var/log/a.txt"); // same file (case-insensitive like local paths)

        Assert.Equal(["sftp://deploy@WEB01/var/log/a.txt"], recent.Files);
    }
}

public sealed class RemoteHostTests
{
    private static RemoteHost Ssh(string address = "web01", string user = "deploy") => new() { Address = address, UserName = user };

    [Fact]
    public void A_complete_ssh_host_is_valid() => Assert.Null(Ssh().Validate());

    [Theory]
    [InlineData("", "deploy")]
    [InlineData("web 01", "deploy")]
    [InlineData("deploy@web01", "deploy")]
    [InlineData("web01/logs", "deploy")]
    [InlineData("web01", "")]
    [InlineData("web01", "de ploy")]
    public void Bad_ssh_fields_are_explained(string address, string user) => Assert.NotNull(Ssh(address, user).Validate());

    [Fact]
    public void Port_and_start_folder_are_checked()
    {
        Assert.NotNull((Ssh() with { Port = 0 }).Validate());
        Assert.NotNull((Ssh() with { Port = 65536 }).Validate());
        Assert.NotNull((Ssh() with { StartFolder = "var/log" }).Validate());
        Assert.Null((Ssh() with { StartFolder = "/var/log" }).Validate());
        Assert.Null((Ssh() with { StartFolder = "~/logs" }).Validate());
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\logs", true)]
    [InlineData(@"\\server", false)]
    [InlineData(@"C:\logs", false)]
    [InlineData("", false)]
    public void A_share_needs_server_and_share(string address, bool valid)
        => Assert.Equal(valid, new RemoteHost { Kind = RemoteHostKind.WindowsShare, Address = address }.Validate() is null);

    [Fact]
    public void Normalised_trims_and_names_the_host()
    {
        var host = new RemoteHost { Address = "  web01 ", UserName = " deploy ", KeyFile = "\"C:\\keys\\id\"", StartFolder = " /var/log " }.Normalised();
        Assert.Equal("web01", host.Name);
        Assert.Equal("web01", host.Address);
        Assert.Equal("deploy", host.UserName);
        Assert.Equal("C:\\keys\\id", host.KeyFile);
        Assert.Equal("/var/log", host.StartFolder);

        var share = new RemoteHost { Kind = RemoteHostKind.WindowsShare, Address = @"\\srv\logs\" }.Normalised();
        Assert.Equal(@"\\srv\logs", share.Address);
    }

    [Fact]
    public void Summary_and_serves()
    {
        var host = Ssh() with { Port = 2222 };
        Assert.Equal("deploy@web01:2222", host.Summary);
        Assert.True(host.Serves(new RemoteLocation("deploy", "WEB01", 2222, "/x")));
        Assert.False(host.Serves(new RemoteLocation("deploy", "web01", 22, "/x")));
        Assert.False(host.Serves(new RemoteLocation("root", "web01", 2222, "/x")));
    }
}

public sealed class FilePatternsTests
{
    [Fact]
    public void Splits_and_matches()
    {
        var patterns = FilePatterns.Split(" *.txt ; app-*.log,");
        Assert.Equal(["*.txt", "app-*.log"], patterns);
        Assert.True(FilePatterns.MatchesAny(patterns, "LOG20261120.TXT"));
        Assert.True(FilePatterns.MatchesAny(patterns, "app-1.log"));
        Assert.False(FilePatterns.MatchesAny(patterns, "other.log"));
    }

    [Fact]
    public void Empty_text_means_the_defaults() => Assert.Equal(FilePatterns.Default.Split(';'), FilePatterns.Split("  "));
}
