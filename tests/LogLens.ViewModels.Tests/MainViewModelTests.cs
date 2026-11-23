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
        Assert.True(streams.Opened[0].Disposed); // the previous log stopped reading its file
    });

    [Fact]
    public Task Only_the_first_of_several_files_opens_and_a_notice_says_so() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1, Path2, @"C:\third.txt"]);

        Assert.Equal([Path1], streams.Requested);
        Assert.Equal(NoticeSeverity.Informational, vm.Notice?.Severity);
        Assert.Contains("2 other files were not opened", vm.Notice?.Message);
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
    public Task A_newer_open_wins_over_an_older_one() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var slow = streams.Waits(Path1);
        streams.Returns(Path2);
        var vm = TestSupport.Main(streams);

        var first = vm.OpenAsync([Path1]);
        await vm.OpenAsync([Path2]);
        slow.Load(TestSupport.Text);
        await first;

        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.False(vm.IsLoading);
        Assert.True(slow.Disposed);
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
