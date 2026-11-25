using LogLens.Core.Remote;

namespace LogLens.ViewModels.Tests;

public sealed class RemoteBrowserViewModelTests
{
    /// <summary>Folders in memory; a listing can be held back to test which of two overlapping listings wins.</summary>
    private sealed class FakeFolders : IRemoteFolders
    {
        public Dictionary<string, RemoteEntry[]> Content { get; } = new()
        {
            ["/var/log"] =
            [
                new RemoteEntry("app", "/var/log/app", true, 0, default),
                new RemoteEntry("syslog", "sftp://u@h/var/log/syslog", false, 10, default),
                new RemoteEntry("Boot.log", "sftp://u@h/var/log/Boot.log", false, 10, default),
                new RemoteEntry("apt", "/var/log/apt", true, 0, default),
                new RemoteEntry("a.txt", "sftp://u@h/var/log/a.txt", false, 10, default),
            ],
            ["/var/log/app"] = [new RemoteEntry("log20261120.txt", "sftp://u@h/var/log/app/log20261120.txt", false, 10, default)],
        };

        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        public string DisplayName => "web01";

        public string StartFolder => "/var/log";

        public string? Parent(string folder) => folder == "/" ? null : RemoteLocation.Parent(folder);

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        {
            if (Gates.TryGetValue(folder, out var gate))
            {
                await gate.Task;
            }

            return folder switch
            {
                "/secret" => throw new UnauthorizedAccessException(),
                "/offline" => throw new IOException("Lost the connection to web01."),
                _ when Content.TryGetValue(folder, out var entries) => entries,
                _ => throw new DirectoryNotFoundException(),
            };
        }
    }

    [Fact]
    public async Task Lists_folders_first_then_matching_files_by_name()
    {
        var vm = new RemoteBrowserViewModel(new FakeFolders(), "*.log;*.txt");
        await vm.LoadAsync();

        Assert.Equal("/var/log", vm.Folder);
        Assert.Equal(["app", "apt", "a.txt", "Boot.log"], vm.Entries.Select(e => e.Name));
        Assert.Equal("2 folders, 2 matching files (1 other file hidden by the pattern)", vm.Summary);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Changing_the_pattern_filters_again_without_listing_again()
    {
        var folders = new FakeFolders();
        var vm = new RemoteBrowserViewModel(folders, "*.txt");
        await vm.LoadAsync();
        folders.Content["/var/log"] = []; // a second listing would see nothing

        vm.Patterns = "*";
        Assert.Equal(["app", "apt", "a.txt", "Boot.log", "syslog"], vm.Entries.Select(e => e.Name));

        vm.Patterns = "   ";
        Assert.Equal(FilePatterns.Default, vm.Patterns);
    }

    /// <summary>Files with known times: oldest is syslog, then Boot.log, a.txt (same time as Boot.log), newest is b.txt.</summary>
    private static FakeFolders FoldersWithTimes()
    {
        var folders = new FakeFolders();
        folders.Content["/var/log"] =
        [
            new RemoteEntry("app", "/var/log/app", true, 0, new DateTime(2026, 11, 20)),
            new RemoteEntry("apt", "/var/log/apt", true, 0, new DateTime(2026, 1, 1)),
            new RemoteEntry("syslog.log", "sftp://u@h/var/log/syslog.log", false, 10, new DateTime(2026, 11, 1)),
            new RemoteEntry("Boot.log", "sftp://u@h/var/log/Boot.log", false, 10, new DateTime(2026, 11, 10)),
            new RemoteEntry("a.log", "sftp://u@h/var/log/a.log", false, 10, new DateTime(2026, 11, 10)),
            new RemoteEntry("b.log", "sftp://u@h/var/log/b.log", false, 10, new DateTime(2026, 11, 19)),
        ];
        return folders;
    }

    [Fact]
    public async Task Files_are_listed_latest_first_by_default()
    {
        var vm = new RemoteBrowserViewModel(FoldersWithTimes(), "*.log");
        await vm.LoadAsync();

        Assert.Equal(RemoteSort.Modified, vm.SortBy);
        Assert.True(vm.SortDescending);
        Assert.Equal(["app", "apt", "b.log", "a.log", "Boot.log", "syslog.log"], vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Sorts_files_by_name_either_way_and_keeps_folders_first()
    {
        var vm = new RemoteBrowserViewModel(FoldersWithTimes(), "*.log");
        await vm.LoadAsync();

        vm.Sort(RemoteSort.Name, descending: true);
        Assert.Equal(["app", "apt", "syslog.log", "Boot.log", "b.log", "a.log"], vm.Entries.Select(e => e.Name));

        vm.Sort(RemoteSort.Name, descending: false);
        Assert.Equal(["app", "apt", "a.log", "b.log", "Boot.log", "syslog.log"], vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Sorts_files_by_last_modified_time_with_ties_by_name()
    {
        var folders = FoldersWithTimes();
        var vm = new RemoteBrowserViewModel(folders, "*.log");
        await vm.LoadAsync();
        folders.Content["/var/log"] = []; // sorting must not list the folder again

        vm.Sort(RemoteSort.Modified, descending: true);
        Assert.Equal(RemoteSort.Modified, vm.SortBy);
        Assert.True(vm.SortDescending);
        Assert.Equal(["app", "apt", "b.log", "a.log", "Boot.log", "syslog.log"], vm.Entries.Select(e => e.Name));

        vm.Sort(RemoteSort.Modified, descending: false);
        Assert.Equal(["app", "apt", "syslog.log", "a.log", "Boot.log", "b.log"], vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task The_sort_order_survives_opening_another_folder()
    {
        var folders = FoldersWithTimes();
        folders.Content["/var/log/app"] =
        [
            new RemoteEntry("old.log", "sftp://u@h/var/log/app/old.log", false, 1, new DateTime(2026, 1, 1)),
            new RemoteEntry("new.log", "sftp://u@h/var/log/app/new.log", false, 1, new DateTime(2026, 2, 1)),
        ];
        var vm = new RemoteBrowserViewModel(folders, "*.log");
        await vm.LoadAsync();
        vm.Sort(RemoteSort.Modified, descending: true);

        await vm.OpenFolderAsync("/var/log/app");
        Assert.Equal(["new.log", "old.log"], vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task The_filter_keeps_files_whose_name_contains_the_text_and_leaves_folders_alone()
    {
        var folders = FoldersWithTimes();
        var vm = new RemoteBrowserViewModel(folders, "*.log");
        await vm.LoadAsync();

        vm.Filter = "  BO ";
        Assert.Equal("BO", vm.Filter);
        Assert.Equal(["app", "apt", "Boot.log"], vm.Entries.Select(e => e.Name));
        Assert.Equal("2 folders, 1 matching file (3 files hidden by the filter)", vm.Summary);

        vm.Filter = "zzz";
        Assert.Equal(["app", "apt"], vm.Entries.Select(e => e.Name));

        vm.Filter = string.Empty;
        Assert.Equal(6, vm.Entries.Count);
    }

    [Fact]
    public async Task The_filter_applies_on_top_of_the_pattern_and_to_every_folder_opened()
    {
        var vm = new RemoteBrowserViewModel(new FakeFolders(), "*.txt");
        await vm.LoadAsync();

        vm.Filter = "log"; // Boot.log and syslog match the text but not the pattern; a.txt matches the pattern but not the text
        Assert.Equal(["app", "apt"], vm.Entries.Select(e => e.Name));
        Assert.Equal("2 folders, 0 matching files (2 other files hidden by the pattern, 1 file hidden by the filter)", vm.Summary);

        await vm.OpenFolderAsync("/var/log/app");
        Assert.Equal("log20261120.txt", Assert.Single(vm.Entries).Name);
    }

    [Fact]
    public async Task Navigates_into_folders_and_up()
    {
        var vm = new RemoteBrowserViewModel(new FakeFolders());
        await vm.LoadAsync();

        await vm.OpenFolderAsync("/var/log/app");
        Assert.Equal("log20261120.txt", Assert.Single(vm.Entries).Name);
        Assert.True(vm.CanGoUp);

        await vm.UpAsync();
        Assert.Equal("/var/log", vm.Folder);
    }

    [Theory]
    [InlineData("/secret", "You don't have permission to read this folder.")]
    [InlineData("/nowhere", "This folder doesn't exist.")]
    [InlineData("/offline", "Lost the connection to web01.")]
    public async Task Explains_a_folder_that_cannot_be_listed(string folder, string error)
    {
        var vm = new RemoteBrowserViewModel(new FakeFolders());
        await vm.LoadAsync();

        await vm.OpenFolderAsync(folder);
        Assert.Equal(folder, vm.Folder);
        Assert.Empty(vm.Entries);
        Assert.Equal(error, vm.Error);
        Assert.Equal(string.Empty, vm.Summary);
    }

    [Fact]
    public async Task Only_the_latest_listing_is_shown()
    {
        var folders = new FakeFolders();
        var slow = new TaskCompletionSource();
        folders.Gates["/var/log"] = slow;
        var vm = new RemoteBrowserViewModel(folders);

        var first = vm.LoadAsync(); // still waiting
        await vm.OpenFolderAsync("/var/log/app");
        slow.SetResult();
        await first;

        Assert.Equal("/var/log/app", vm.Folder);
        Assert.Equal("log20261120.txt", Assert.Single(vm.Entries).Name);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task The_path_shows_each_folder_from_the_top_and_follows_navigation()
    {
        var vm = new RemoteBrowserViewModel(new FakeFolders());
        Assert.Equal([new RemoteCrumb("/", "/"), new RemoteCrumb("var", "/var"), new RemoteCrumb("log", "/var/log")], vm.Crumbs);

        await vm.OpenFolderAsync("/var/log/app");
        Assert.Equal(["/", "var", "log", "app"], vm.Crumbs.Select(c => c.Name));
        Assert.Equal("/var/log/app", vm.Crumbs[^1].Path);

        await vm.OpenFolderAsync(vm.Crumbs[1].Path);
        Assert.Equal("/var", vm.Folder);
        Assert.Equal(["/", "var"], vm.Crumbs.Select(c => c.Name));
    }

    [Fact]
    public void A_share_path_starts_at_the_share()
    {
        var share = new ShareLike();
        Assert.Equal(
            [new RemoteCrumb(@"\\srv\logs", @"\\srv\logs"), new RemoteCrumb("app", @"\\srv\logs\app"), new RemoteCrumb("2026", @"\\srv\logs\app\2026")],
            RemoteBrowserViewModel.BuildCrumbs(share, @"\\srv\logs\app\2026"));
    }

    [Fact]
    public async Task Sub_folders_of_a_part_of_the_path_are_listed_without_moving()
    {
        var folders = new FakeFolders();
        folders.Content["/var"] = [new RemoteEntry("log", "/var/log", true, 0, default), new RemoteEntry("cache", "/var/cache", true, 0, default), new RemoteEntry("x.txt", "sftp://u@h/var/x.txt", false, 1, default)];
        var vm = new RemoteBrowserViewModel(folders);
        await vm.LoadAsync();

        var parent = await vm.ListSubFoldersAsync("/var", CancellationToken.None);
        Assert.Null(parent.Error);
        Assert.Equal(["cache", "log"], parent.Folders.Select(f => f.Name));
        Assert.Equal("/var/log", vm.Folder);
        Assert.Equal(4, vm.Entries.Count);

        folders.Content["/var/log"] = []; // the shown folder's come from its listing, not a new one
        Assert.Equal(["app", "apt"], (await vm.ListSubFoldersAsync("/var/log", CancellationToken.None)).Folders.Select(f => f.Name));

        var denied = await vm.ListSubFoldersAsync("/secret", CancellationToken.None);
        Assert.Empty(denied.Folders);
        Assert.Equal("You don't have permission to read this folder.", denied.Error);
    }

    /// <summary>Parents like <c>ShareFolders</c>: the top is <c>\\server\share</c>.</summary>
    private sealed class ShareLike : IRemoteFolders
    {
        public string DisplayName => "srv";

        public string StartFolder => @"\\srv\logs";

        public string? Parent(string folder) => folder.TrimEnd('\\') == @"\\srv\logs" ? null : folder[..folder.TrimEnd('\\').LastIndexOf('\\')];

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]);
    }
}
