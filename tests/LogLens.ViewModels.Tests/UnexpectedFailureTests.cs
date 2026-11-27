namespace LogLens.ViewModels.Tests;

/// <summary>A failure that is not a read error (a bug in a parser, say) never leaves an open hanging: it is reported and logged (UX-3, NFR-12).</summary>
public class UnexpectedFailureTests
{
    private const string Path1 = @"C:\logs\app.txt";
    private const string Path2 = @"C:\logs\other.txt";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task Within(Task task)
        => Assert.True(await Task.WhenAny(task, Task.Delay(Patience)) == task, "the open never finished");

    [Fact]
    public Task An_unexpected_failure_before_anything_was_read_ends_the_open_with_a_notice() => UiThread.Run(async () =>
    {
        var failure = new InvalidOperationException("Event id 7 does not follow 5.");
        var streams = new FakeStreams();
        streams.Returns(Path2);
        streams.Throws(Path1, failure);
        var vm = TestSupport.Main(streams);
        var logged = new List<Exception>();
        vm.BackgroundError += (_, ex) => logged.Add(ex);
        await vm.OpenAsync([Path2]);

        await Within(vm.OpenAsync([Path1]));

        Assert.Equal([Path2], vm.Tabs.Select(t => t.Document.FilePath));
        Assert.False(vm.IsLoading);
        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal("Could not open app.txt", vm.Notice?.Title);
        Assert.Contains("diagnostics log", vm.Notice?.Message);
        Assert.Same(failure, Assert.Single(logged));
        Assert.True(streams.Opened[1].Disposed);
    });

    [Fact]
    public Task An_unexpected_failure_after_the_tab_was_shown_keeps_what_was_read_and_says_reading_stopped() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var stream = streams.Waits(Path1);
        var vm = TestSupport.Main(streams);
        var logged = new List<Exception>();
        vm.BackgroundError += (_, ex) => logged.Add(ex);
        var open = vm.OpenAsync([Path1]);
        stream.Load(TestSupport.Text);
        await Within(open);
        var log = Assert.Single(vm.Tabs);
        var events = log.Document.Events.Count;

        var failure = new InvalidOperationException("Delta starts at event 9 but the document only has 3.");
        stream.Fail(failure);
        await log.Stopped;

        Assert.Same(log, Assert.Single(vm.Tabs));
        Assert.True(log.IsStopped);
        Assert.Equal(events, log.Document.Events.Count);
        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.StartsWith("Stopped reading", vm.Notice?.Title);
        Assert.Same(failure, Assert.Single(logged));
    });

    [Fact]
    public Task A_read_error_is_reported_but_not_logged_as_unexpected() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Throws(Path1, new IOException("The device is not ready."));
        var vm = TestSupport.Main(streams);
        var logged = new List<Exception>();
        vm.BackgroundError += (_, ex) => logged.Add(ex);

        await Within(vm.OpenAsync([Path1]));

        Assert.Equal("Could not open app.txt", vm.Notice?.Title);
        Assert.Equal("The device is not ready.", vm.Notice?.Message);
        Assert.Empty(logged);
    });
}
