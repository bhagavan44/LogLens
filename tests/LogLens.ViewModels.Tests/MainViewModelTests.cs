using LogLens.Core.State;

namespace LogLens.ViewModels.Tests;

public class MainViewModelTests
{
    private const string Path1 = @"C:\logs\app.txt";
    private const string Path2 = @"C:\logs\other.txt";

    [Fact]
    public void Starts_empty()
    {
        var vm = TestSupport.Main(new FakeStreams());

        Assert.False(vm.HasLog);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
        Assert.Null(vm.Notice);
        Assert.Equal("LogLens", vm.WindowTitle);
        Assert.Empty(vm.RecentFiles);
    }

    [Fact]
    public Task Opening_a_file_shows_it() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1]);

        Assert.Equal(Path1, vm.CurrentLog?.Document.FilePath);
        Assert.Equal(3, vm.CurrentLog?.Document.Events.Count);
        Assert.False(vm.CurrentLog?.IsLoading);
        Assert.False(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
        Assert.Null(vm.Notice);
        Assert.Equal("app.txt — LogLens", vm.WindowTitle);
    });

    [Fact]
    public Task Until_the_first_rows_arrive_the_current_log_stays_and_the_file_is_named() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path2);
        var pending = streams.Waits(Path1);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path2]);

        var open = vm.OpenAsync([Path1]);

        Assert.True(vm.IsLoading);
        Assert.False(vm.ShowEmptyState);
        Assert.Equal("Opening app.txt…", vm.LoadingText);
        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);

        pending.Write(TestSupport.Lines(0, 9)); // the first chunk of a larger file
        await open;

        Assert.False(vm.IsLoading);
        Assert.Equal(Path1, vm.CurrentLog?.Document.FilePath);
        Assert.True(vm.CurrentLog?.IsLoading); // still reading: the rows show while the rest loads (UX-4)
        Assert.Equal([Path2, Path1], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.False(streams.Opened[0].Disposed); // the other tab keeps reading its file
    });

    // ------------------------------------------------------------------ tabs (FR-O2)

    [Fact]
    public Task Several_files_open_in_tabs_in_order_with_the_last_in_front() => UiThread.Run(async () =>
    {
        const string path3 = @"C:\logs\third.txt";
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        streams.Returns(path3);
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1, Path2, path3]);

        Assert.Equal([Path1, Path2, path3], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.Same(vm.Tabs[2], vm.CurrentLog);
        Assert.Null(vm.Notice);
    });

    [Fact]
    public Task A_file_that_is_already_open_comes_to_the_front_instead_of_opening_twice() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path1, Path2]);

        await vm.OpenAsync([Path1.ToUpperInvariant()]);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[0], vm.CurrentLog);
        Assert.Equal(2, streams.Requested.Count);
    });

    [Fact]
    public Task Only_the_first_files_of_a_very_large_set_open_and_a_notice_says_so() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var paths = Enumerable.Range(1, MainViewModel.MaxTabsPerOpen + 3).Select(i => $@"C:\logs\f{i}.txt").ToList();
        paths.ForEach(p => streams.Returns(p));
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync(paths);

        Assert.Equal(MainViewModel.MaxTabsPerOpen, vm.Tabs.Count);
        Assert.Equal($"Opened the first {MainViewModel.MaxTabsPerOpen} files", vm.Notice?.Title);
        Assert.Contains("3 more were not opened", vm.Notice?.Message);
    });

    [Fact]
    public Task One_file_that_fails_does_not_stop_the_others() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path2);
        var vm = TestSupport.Main(streams); // Path1 is missing

        await vm.OpenAsync([Path1, Path2]);

        Assert.Equal([Path2], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.Equal("File not found", vm.Notice?.Title);
    });

    [Fact]
    public Task Closing_a_tab_brings_its_neighbour_to_the_front_and_stops_reading_its_file() => UiThread.Run(async () =>
    {
        const string path3 = @"C:\logs\third.txt";
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        streams.Returns(path3);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path1, Path2, path3]);
        vm.CurrentLog = vm.Tabs[1];

        vm.Close();

        Assert.Equal([Path1, path3], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.Equal(path3, vm.CurrentLog?.Document.FilePath);
        Assert.True(streams.Opened[1].Disposed);
        Assert.False(streams.Opened[0].Disposed);

        vm.CloseOthers(vm.Tabs[0]);
        Assert.Equal([Path1], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.Same(vm.Tabs[0], vm.CurrentLog);
    });

    [Fact]
    public Task Tabs_can_be_moved_and_stepped_through() => UiThread.Run(async () =>
    {
        const string path3 = @"C:\logs\third.txt";
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        streams.Returns(path3);
        var store = new MemoryStateStore();
        var vm = TestSupport.Main(streams, store);
        await vm.OpenAsync([Path1, Path2, path3]);

        vm.MoveTab(2, 0);
        Assert.Equal([path3, Path1, Path2], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.Equal([path3, Path1, Path2], store.State.Session.Tabs.Select(t => t.Path));

        vm.CurrentLog = vm.Tabs[2];
        vm.SelectNextTab(forward: true);
        Assert.Same(vm.Tabs[0], vm.CurrentLog);
        vm.SelectNextTab(forward: false);
        Assert.Same(vm.Tabs[2], vm.CurrentLog);
    });

    [Fact]
    public Task A_notice_from_a_tab_in_the_background_names_the_tab() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var background = streams.Waits(Path1);
        streams.Returns(Path2);
        var vm = TestSupport.Main(streams);
        var open = vm.OpenAsync([Path1]);
        background.Load(TestSupport.Text);
        await open;
        await vm.OpenAsync([Path2]);

        background.Reset();
        await Eventually.True(() => vm.Notice is not null, "the reset notice");

        Assert.Equal("app.txt: app.txt was read again", vm.Notice!.Title);
    });

    // ------------------------------------------------------------------ merge view (FR-O4)

    [Fact]
    public Task Merging_files_opens_one_tab_with_their_events_in_time_order() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1, TestSupport.Line(0) + TestSupport.Line(10));
        streams.Returns(Path2, TestSupport.Line(5));
        var vm = TestSupport.Main(streams);

        await vm.OpenMergedAsync([Path1, Path2]);

        var log = Assert.Single(vm.Tabs);
        Assert.True(log.IsMerged);
        Assert.Equal(["event 0", "event 5", "event 10"], log.Events.Select(e => e.Message));
        Assert.Equal([Path1, Path2, Path1], log.Events.Select(e => e.SourceFile));
        Assert.Equal("app.txt + 1 more", MainViewModel.TabTitle(log));
        Assert.Equal($"{Path1}\n{Path2}", MainViewModel.TabToolTip(log));
        Assert.False(log.CanFollow); // a snapshot
        Assert.Equal(2, log.Document.LineNumberOf(log.Events[2])); // line numbers count per file
    });

    [Fact]
    public Task The_same_merge_again_comes_to_the_front() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        var vm = TestSupport.Main(streams);
        await vm.OpenMergedAsync([Path1, Path2]);
        await vm.OpenAsync([Path1]);

        await vm.OpenMergedAsync([Path2, Path1]);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.True(vm.CurrentLog?.IsMerged);
        Assert.Single(streams.MergeRequests);
    });

    [Fact]
    public Task Merging_one_file_just_opens_it() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);

        await vm.OpenMergedAsync([Path1, Path1]);

        Assert.False(vm.CurrentLog?.IsMerged);
        Assert.Empty(streams.MergeRequests);
    });

    [Fact]
    public Task A_merge_with_a_missing_file_names_that_file() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);

        await vm.OpenMergedAsync([Path1, Path2]);

        Assert.Empty(vm.Tabs);
        Assert.Equal("File not found", vm.Notice?.Title);
        Assert.Contains(Path2, vm.Notice?.Message);
        Assert.False(vm.IsLoading);
    });

    [Theory]
    [InlineData(typeof(FileNotFoundException), "File not found")]
    [InlineData(typeof(DirectoryNotFoundException), "File not found")]
    [InlineData(typeof(UnauthorizedAccessException), "Access denied")]
    [InlineData(typeof(IOException), "Could not open app.txt")]
    public Task Errors_become_a_notice_and_keep_the_current_log(Type exceptionType, string title) => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path2);
        streams.Throws(Path1, (Exception)Activator.CreateInstance(exceptionType)!);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path2]);

        await vm.OpenAsync([Path1]);

        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal(title, vm.Notice?.Title);
        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.False(vm.IsLoading);
        Assert.False(streams.Opened[0].Disposed);
        Assert.Equal([Path2], vm.RecentFiles);
    });

    [Fact]
    public Task A_file_where_nothing_parses_gets_a_format_warning() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1, "plain text\nnot serilog\n");
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1]);

        Assert.True(vm.HasLog); // the lines are still shown (FR-F5)
        Assert.Equal(NoticeSeverity.Warning, vm.Notice?.Severity);
        Assert.Equal("0 events parsed — check the format", vm.Notice?.Title);
    });

    [Fact]
    public Task An_empty_file_says_so() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1, string.Empty);
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1]);

        Assert.True(vm.HasLog);
        Assert.Equal("The file is empty", vm.Notice?.Title);
    });

    [Fact]
    public Task Cancelling_an_open_keeps_the_previous_log_without_a_notice() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path2);
        var pending = streams.Waits(Path1);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path2]);

        var open = vm.OpenAsync([Path1]);
        vm.CancelLoad();
        await open;

        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.Null(vm.Notice);
        Assert.False(vm.IsLoading);
        Assert.True(pending.Disposed);
    });

    [Fact]
    public Task Two_opens_at_once_both_get_a_tab_and_the_loading_text_counts_them() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var slow = streams.Waits(Path1);
        var slower = streams.Waits(Path2);
        var vm = TestSupport.Main(streams);

        var first = vm.OpenAsync([Path1]);
        var second = vm.OpenAsync([Path2]);
        Assert.Equal("Opening 2 logs…", vm.LoadingText);

        slower.Load(TestSupport.Text);
        await second;
        Assert.Equal("Opening app.txt…", vm.LoadingText);
        slow.Load(TestSupport.Text);
        await first;

        Assert.Equal([Path2, Path1], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.False(vm.IsLoading);
        Assert.False(slow.Disposed);
    });

    [Fact]
    public Task Closing_returns_to_the_empty_state_and_stops_reading() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([Path1]);

        vm.Close();

        Assert.False(vm.HasLog);
        Assert.True(vm.ShowEmptyState);
        Assert.Equal("LogLens", vm.WindowTitle);
        Assert.True(streams.Opened[0].Disposed);
    });

    [Fact]
    public async Task Opening_nothing_does_nothing()
    {
        var streams = new FakeStreams();
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([]);

        Assert.Empty(streams.Requested);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void Unexpected_errors_are_reported_as_a_notice()
    {
        var vm = TestSupport.Main(new FakeStreams());

        vm.ReportUnexpectedError(new InvalidOperationException("bug"));

        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal("bug", vm.Notice?.Message);
    }

    // ------------------------------------------------------------------ recent files (FR-O1)

    [Fact]
    public Task Opened_files_become_recent_files_newest_first() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        var store = new MemoryStateStore();
        var vm = TestSupport.Main(streams, store);
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MainViewModel.RecentFiles) ? 1 : 0;

        await vm.OpenAsync([Path1]);
        await vm.OpenAsync([Path2]);

        Assert.Equal([Path2, Path1], vm.RecentFiles);
        Assert.Equal([Path2, Path1], store.State.RecentFiles);
        Assert.Equal(2, changes);

        vm.ClearRecentFiles();
        Assert.Empty(vm.RecentFiles);
    });

    [Fact]
    public Task A_recent_file_that_is_gone_is_removed_from_the_list() => UiThread.Run(async () =>
    {
        var store = new MemoryStateStore { State = new AppState { RecentFiles = [Path1, Path2] } };
        var vm = TestSupport.Main(new FakeStreams(), store); // every file is missing

        await vm.OpenAsync([Path1]);

        Assert.Equal("File not found", vm.Notice?.Title);
        Assert.Equal([Path2], vm.RecentFiles);
    });

    // ------------------------------------------------------------------ session (FR-P3)

    [Fact]
    public Task The_open_log_and_its_tail_are_remembered() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var store = new MemoryStateStore();
        var vm = TestSupport.Main(streams, store);

        await vm.OpenAsync([Path1]);
        Assert.Equal(Path1, store.State.Session.Tabs.Single().Path);
        Assert.False(store.State.Session.Tabs.Single().Follow);

        vm.CurrentLog!.IsFollowing = true;
        Assert.True(store.State.Session.Tabs.Single().Follow);

        vm.Close();
        Assert.Empty(store.State.Session.Tabs);
    });

    [Fact]
    public Task The_last_session_is_reopened_with_tail_as_it_was() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var store = new MemoryStateStore
        {
            State = new AppState { Session = new SessionState { Tabs = [new SessionTab { Path = Path1, Follow = true }] } },
        };
        var vm = TestSupport.Main(streams, store);

        await vm.RestoreSessionAsync();

        Assert.Equal(Path1, vm.CurrentLog?.Document.FilePath);
        Assert.True(vm.CurrentLog?.IsFollowing);
        Assert.True(streams.Opened.Single().Follow);
    });

    [Fact]
    public Task Every_tab_and_the_one_in_front_are_remembered_merged_views_included() => UiThread.Run(async () =>
    {
        const string path3 = @"C:\logs\third.txt";
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        streams.Returns(path3);
        var store = new MemoryStateStore();
        var vm = TestSupport.Main(streams, store);

        await vm.OpenAsync([Path1]);
        await vm.OpenMergedAsync([Path2, path3]);
        vm.CurrentLog = vm.Tabs[0];

        var session = store.State.Session;
        Assert.Equal(2, session.Tabs.Count);
        Assert.Equal(Path1, session.Tabs[0].Path);
        Assert.Equal([Path2, path3], session.Tabs[1].MergedFiles);
        Assert.Equal(0, session.Selected);
    });

    [Fact]
    public Task A_session_of_several_tabs_reopens_in_order_with_the_same_tab_in_front() => UiThread.Run(async () =>
    {
        const string path3 = @"C:\logs\third.txt";
        var streams = new FakeStreams();
        streams.Returns(Path1);
        streams.Returns(Path2);
        streams.Returns(path3);
        var store = new MemoryStateStore
        {
            State = new AppState
            {
                Session = new SessionState
                {
                    Tabs = [new SessionTab { MergedFiles = [Path2, path3] }, new SessionTab { Path = @"C:\gone.txt" }, new SessionTab { Path = Path1 }],
                    Selected = 0,
                },
            },
        };
        var vm = TestSupport.Main(streams, store);

        await vm.RestoreSessionAsync();

        Assert.Equal(2, vm.Tabs.Count); // the missing file is reported, not opened
        Assert.True(vm.Tabs[0].IsMerged);
        Assert.Equal(Path1, vm.Tabs[1].Document.FilePath);
        Assert.Same(vm.Tabs[0], vm.CurrentLog);
        Assert.Equal(2, store.State.Session.Tabs.Count);
        Assert.Equal("File not found", vm.Notice?.Title);
    });

    [Fact]
    public async Task An_empty_session_opens_nothing()
    {
        var streams = new FakeStreams();
        var vm = TestSupport.Main(streams);

        await vm.RestoreSessionAsync();

        Assert.Empty(streams.Requested);
        Assert.True(vm.ShowEmptyState);
    }

    [Fact]
    public Task A_window_that_does_not_remember_the_session_leaves_it_alone() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path2);
        var store = new MemoryStateStore
        {
            State = new AppState { Session = new SessionState { Tabs = [new SessionTab { Path = Path1 }] } },
        };
        var vm = TestSupport.Main(streams, store);
        vm.RemembersSession = false;

        await vm.OpenAsync([Path2]);

        Assert.Equal(Path1, store.State.Session.Tabs.Single().Path);
    });

    // ------------------------------------------------------------------ live log

    [Fact]
    public Task Notices_from_the_log_reach_the_message_bar() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var stream = streams.Waits(Path1);
        var vm = TestSupport.Main(streams);
        var open = vm.OpenAsync([Path1]);
        stream.Load(TestSupport.Text);
        await open;

        stream.Reset();
        await Eventually.True(() => vm.Notice is not null, "the reset notice");

        Assert.Equal("app.txt was read again", vm.Notice!.Title);
    });

    [Fact]
    public Task A_log_whose_file_fails_later_says_it_stopped_and_stays_open() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var stream = streams.Waits(Path1);
        var vm = TestSupport.Main(streams);
        var open = vm.OpenAsync([Path1]);
        stream.Load(TestSupport.Text);
        await open;

        stream.Fail(new UnauthorizedAccessException("Access to the path is denied."));
        await Eventually.True(() => vm.Notice is not null, "the failure notice");

        Assert.Equal("Stopped reading app.txt", vm.Notice!.Title);
        Assert.True(vm.HasLog);
        Assert.True(vm.CurrentLog!.IsStopped);
        Assert.False(vm.CurrentLog.CanFollow);
    });
}
