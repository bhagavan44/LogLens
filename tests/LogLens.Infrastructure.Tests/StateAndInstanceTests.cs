using System.Collections.Concurrent;
using LogLens.Core.State;
using LogLens.Infrastructure.Instances;
using LogLens.Infrastructure.State;

namespace LogLens.Infrastructure.Tests;

public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loglens-tests", Guid.NewGuid().ToString("N"));

    private AppPaths Paths => new(_dir, _dir);

    private string StatePath => Paths.StateFile;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_gives_an_empty_state()
    {
        var state = new JsonStateStore(Paths).Load();

        Assert.Empty(state.RecentFiles);
        Assert.Empty(state.Session.Tabs);
    }

    [Fact]
    public void State_round_trips_through_the_file()
    {
        var store = new JsonStateStore(Paths);

        store.Save(new AppState
        {
            RecentFiles = [@"C:\logs\a.txt", @"C:\logs\b.txt"],
            Session = new SessionState { Tabs = [new SessionTab { Path = @"C:\logs\a.txt", Follow = true }] },
        });

        var loaded = new JsonStateStore(Paths).Load();
        Assert.Equal([@"C:\logs\a.txt", @"C:\logs\b.txt"], loaded.RecentFiles);
        var tab = Assert.Single(loaded.Session.Tabs);
        Assert.Equal(@"C:\logs\a.txt", tab.Path);
        Assert.True(tab.Follow);
        Assert.StartsWith(Path.Combine(_dir, "LogLens"), StatePath);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{{{ nope");

        var state = new JsonStateStore(Paths).Load();

        Assert.Empty(state.RecentFiles);
        Assert.True(File.Exists(StatePath + ".corrupt"));
    }

    [Fact]
    public void Nulls_in_a_hand_edited_file_become_empty_lists()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{ \"recentFiles\": null, \"session\": { \"tabs\": null } }");

        var state = new JsonStateStore(Paths).Load();

        Assert.Empty(state.RecentFiles);
        Assert.Empty(state.Session.Tabs);
        Assert.Empty(new SessionService(new JsonStateStore(Paths)).Current.Tabs);
    }

    [Fact]
    public void Recent_files_and_the_session_persist_through_the_real_store()
    {
        var first = new RecentFilesService(new JsonStateStore(Paths));
        first.Add(@"C:\logs\one.txt");
        first.Add(@"C:\logs\two.txt");
        new SessionService(new JsonStateStore(Paths)).Save(new SessionState { Tabs = [new SessionTab { Path = @"C:\logs\two.txt" }] });

        var store = new JsonStateStore(Paths);
        Assert.Equal([@"C:\logs\two.txt", @"C:\logs\one.txt"], new RecentFilesService(store).Files);
        Assert.Equal(@"C:\logs\two.txt", new SessionService(store).Current.Tabs.Single().Path); // saving the session kept the recent files
    }
}

public class AppPathsTests
{
    [Fact]
    public void Without_data_dir_the_profile_folders_are_used()
    {
        var paths = AppPaths.Resolve(new CommandLine([], null), @"C:\work");

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LogLens"), paths.RoamingRoot);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogLens"), paths.LocalRoot);
    }

    [Fact]
    public void Data_dir_keeps_everything_under_that_folder_read_against_the_working_directory()
    {
        var paths = AppPaths.Resolve(CommandLine.Parse(["--data-dir", "scratch"], @"C:\work"), @"C:\work");

        Assert.Equal(@"C:\work\scratch\LogLens", paths.RoamingRoot);
        Assert.Equal(@"C:\work\scratch\LogLens", paths.LocalRoot);
        Assert.Equal(@"C:\work\scratch\LogLens\state.json", paths.StateFile);
    }

    [Fact]
    public void Data_dir_without_a_folder_is_refused_rather_than_falling_back_to_the_profile()
        => Assert.Throws<ArgumentException>(() => AppPaths.Resolve(CommandLine.Parse(["--data-dir"], @"C:\work"), @"C:\work"));

    [Fact]
    public void Another_data_dir_is_another_instance()
    {
        var a = AppPaths.Resolve(CommandLine.Parse(["--data-dir", @"D:\a"], @"C:\"), @"C:\");
        var b = AppPaths.Resolve(CommandLine.Parse(["--data-dir", @"D:\b"], @"C:\"), @"C:\");

        Assert.NotEqual(a.InstanceKey, b.InstanceKey);
    }
}

public sealed class SingleInstanceTests
{
    private static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Stands in for one LogLens process. A mutex belongs to the thread that took it (and is re-entrant on that thread), so each simulated
    /// process claims on, and later releases from, its own long-lived thread, like the real app's main thread.
    /// </summary>
    private sealed class FakeProcess : IDisposable
    {
        private readonly ManualResetEventSlim _claimed = new();
        private readonly ManualResetEventSlim _exit = new();
        private readonly Thread _thread;

        public FakeProcess(string key, LaunchRequest request, Action<int>? allowForeground = null)
        {
            _thread = new Thread(() =>
            {
                Instance = SingleInstance.Claim(key, request, allowForeground);
                _claimed.Set();
                _exit.Wait();
                Instance?.Dispose();
            });
            _thread.Start();
            _claimed.Wait();
        }

        /// <summary>The claimed instance; null when the request was handed to a running one.</summary>
        public SingleInstance? Instance { get; private set; }

        public void Dispose()
        {
            _exit.Set();
            _thread.Join();
        }
    }

    [Fact]
    public void Relative_paths_are_resolved_against_the_senders_folder_and_options_are_skipped()
    {
        var request = new LaunchRequest(@"C:\work", [@"logs\a.txt", "--data-dir", @"D:\scratch", @"D:\b.txt", " "]);

        Assert.Equal([@"C:\work\logs\a.txt", @"D:\b.txt"], request.ResolvePaths());
    }

    [Fact]
    public void The_first_launch_becomes_primary_and_a_second_hands_its_files_over()
    {
        var key = NewKey();
        var received = new BlockingCollection<LaunchRequest>();
        using var primary = new FakeProcess(key, new LaunchRequest(@"C:\", []));
        Assert.True(primary.Instance!.IsPrimary);
        primary.Instance.Listen(received.Add);

        int? allowedPid = null;
        using var second = new FakeProcess(key, new LaunchRequest(@"C:\work", ["a.txt"]), pid => allowedPid = pid);

        Assert.Null(second.Instance); // delivered: the second process exits
        Assert.True(received.TryTake(out var request, TimeSpan.FromSeconds(5)));
        Assert.Equal([@"C:\work\a.txt"], request!.ResolvePaths());
        Assert.Equal(Environment.ProcessId, allowedPid);
    }

    [Fact]
    public void Requests_that_arrive_before_the_window_listens_are_queued()
    {
        var key = NewKey();
        using var primary = new FakeProcess(key, new LaunchRequest(@"C:\", []));

        using (var early = new FakeProcess(key, new LaunchRequest(@"C:\", ["early.txt"])))
        {
            Assert.Null(early.Instance);
        }

        var received = new List<LaunchRequest>();
        primary.Instance!.Listen(received.Add);
        Assert.Equal(@"C:\early.txt", Assert.Single(received).ResolvePaths().Single());
    }

    [Fact]
    public void After_the_primary_exits_the_next_launch_becomes_primary()
    {
        var key = NewKey();
        new FakeProcess(key, new LaunchRequest(@"C:\", [])).Dispose();

        using var next = new FakeProcess(key, new LaunchRequest(@"C:\", []));

        Assert.True(next.Instance!.IsPrimary);
    }

    [Fact]
    public void Different_keys_do_not_share_an_instance()
    {
        using var a = new FakeProcess(NewKey(), new LaunchRequest(@"C:\", []));
        using var b = new FakeProcess(NewKey(), new LaunchRequest(@"C:\", []));

        Assert.True(a.Instance!.IsPrimary);
        Assert.True(b.Instance!.IsPrimary);
    }
}
