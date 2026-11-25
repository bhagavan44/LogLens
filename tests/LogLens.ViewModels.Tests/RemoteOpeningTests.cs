using LogLens.Core.Models;
using LogLens.Core.Remote;
using LogLens.Core.State;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels.Tests;

/// <summary>FR-O7: the host of an <c>sftp://</c> file is connected before the file opens, from every way of opening it.</summary>
public sealed class RemoteOpeningTests
{
    private const string Remote1 = "sftp://deploy@web01/var/log/a.txt";
    private const string Remote2 = "sftp://deploy@web01/var/log/b.txt";
    private const string Local = @"C:\logs\app.txt";

    /// <summary>Answers every connect with <see cref="Result"/> and records which paths it was asked about.</summary>
    private sealed class FakeConnector : IRemoteConnector
    {
        public RemoteConnectResult Result { get; set; } = RemoteConnectResult.Success;

        public List<string> Asked { get; } = [];

        public Task<RemoteConnectResult> ConnectAsync(string path, CancellationToken cancellationToken)
        {
            Asked.Add(path);
            return Task.FromResult(RemoteLocation.IsRemote(path) ? Result : RemoteConnectResult.Success);
        }
    }

    private static MainViewModel NewMain(FakeStreams streams, FakeConnector connector, MemoryStateStore? store = null)
    {
        store ??= new MemoryStateStore();
        return new MainViewModel(streams, new RecentFilesService(store), new SessionService(store), new BookmarkService(store), remote: connector);
    }

    [Fact]
    public Task A_remote_file_opens_once_its_host_is_connected() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Remote1);
        var connector = new FakeConnector();
        var vm = NewMain(streams, connector);

        await vm.OpenAsync([Remote1, Local]);

        Assert.Equal([Remote1], connector.Asked);
        Assert.Equal(Remote1, streams.Requested[0]);
        Assert.Equal("a.txt", vm.Tabs[0].Document.FileName);
    });

    [Fact]
    public Task Cancelling_the_connection_opens_nothing_and_says_so_quietly() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Remote1);
        var vm = NewMain(streams, new FakeConnector { Result = new RemoteConnectResult(false, Cancelled: true) });

        await vm.OpenAsync([Remote1]);

        Assert.Empty(vm.Tabs);
        Assert.Empty(streams.Requested);
        Assert.Equal(NoticeSeverity.Informational, vm.Notice?.Severity);
        Assert.Equal("Not connected to web01", vm.Notice?.Title);
        Assert.Contains("a.txt was not opened", vm.Notice?.Message);
    });

    [Fact]
    public Task A_host_that_cant_be_reached_is_an_error_with_the_reason() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var vm = NewMain(streams, new FakeConnector { Result = new RemoteConnectResult(false, Problem: "web01 refused the connection on port 22.") });

        await vm.OpenAsync([Remote1]);

        Assert.Empty(vm.Tabs);
        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal("Couldn't connect to web01", vm.Notice?.Title);
        Assert.Equal("web01 refused the connection on port 22.", vm.Notice?.Message);
    });

    [Fact]
    public Task A_merge_connects_each_host_once_before_reading() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Remote1, TestSupport.Lines(1, 2));
        streams.Returns(Remote2, TestSupport.Lines(3, 4));
        streams.Returns(Local, TestSupport.Lines(5, 6));
        var connector = new FakeConnector();
        var vm = NewMain(streams, connector);

        await vm.OpenMergedAsync([Remote1, Remote2, Local]);

        Assert.Equal([Remote1], connector.Asked); // same login: one connection
        Assert.Single(streams.MergeRequests);
        Assert.Equal(6, vm.Tabs.Single().Document.Events.Count);
    });

    [Fact]
    public Task A_restored_remote_tab_connects_first() => UiThread.Run(async () =>
    {
        var store = new MemoryStateStore();
        store.Save(new AppState { Session = new SessionState { Tabs = [new SessionTab { Path = Remote1 }] } });
        var streams = new FakeStreams();
        streams.Returns(Remote1);
        var connector = new FakeConnector();
        var vm = NewMain(streams, connector, store);

        await vm.RestoreSessionAsync();

        Assert.Equal([Remote1], connector.Asked);
        Assert.Equal(Remote1, vm.Tabs.Single().Document.FilePath);
    });

    [Fact]
    public void A_remote_file_that_stops_answering_shows_reconnecting_until_it_answers()
    {
        var log = new LogViewModel(new LogDocument(Remote1, LogLens.Core.Formats.LogFormats.SerilogText)) { IsFollowing = true };
        var parser = LogLens.Core.Formats.LogFormats.SerilogText.CreateParser(Remote1);
        var text = TestSupport.Lines(1, 2);
        log.HandleBatch(new ChunkBatch(text, parser.Append(text), text.Length, text.Length, true, Remote1, "UTF-8"));

        log.HandleBatch(new StatusBatch(StreamState.Reconnecting, "Lost the connection to deploy@web01."));
        Assert.Equal("Reconnecting…", log.TailStatus);

        log.HandleBatch(new StatusBatch(StreamState.Following));
        Assert.Equal("● Live", log.TailStatus);
    }
}
