using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels.Tests;

/// <summary>Progressive loading and live tail in <see cref="LogViewModel"/> (FR-T1 … FR-T7).</summary>
public class StreamingTests
{
    private const string Path = @"C:\logs\log20261113.txt";

    /// <summary>A view-model over an empty document plus a parser that turns text into the batches a stream would deliver.</summary>
    private sealed class Feed
    {
        private ILogParser _parser = LogFormats.SerilogText.CreateParser(Path);
        private long _bytes;

        public LogViewModel Vm { get; } = new(new LogDocument(Path, LogFormats.SerilogText));

        public ChunkBatch Chunk(string text, bool initialLoadComplete = false)
        {
            _bytes += text.Length;
            return new ChunkBatch(text, _parser.Append(text), _bytes, _bytes, initialLoadComplete, Path, "UTF-8");
        }

        public Feed Send(string text, bool initialLoadComplete = false)
        {
            Vm.HandleBatch(Chunk(text, initialLoadComplete));
            return this;
        }

        public void Restart() => (_parser, _bytes) = (LogFormats.SerilogText.CreateParser(Path), 0);

        public string[] Messages() => Vm.Events.Select(e => e.Message).ToArray();
    }

    // ------------------------------------------------------------------ applying batches

    [Fact]
    public void Batches_grow_the_document_and_the_list_in_place()
    {
        var feed = new Feed();
        var changes = new List<RowsChange>();
        feed.Vm.RowsChanged += (_, c) => changes.Add(c);

        feed.Send(TestSupport.Lines(0, 2)).Send(TestSupport.Line(3, "ERR"));

        Assert.Equal(["event 0", "event 1", "event 2", "event 3"], feed.Messages());
        Assert.Equal(new RowsChange(0, 0, 3), changes[0]);
        Assert.Equal(new RowsChange(2, 1, 2), changes[1]); // the provisional last event is replaced, the new one added
        Assert.Equal(1, feed.Vm.LevelToggles.Single(t => t.Level == LogLevel.Error).Count);
        Assert.Equal(3, feed.Vm.LevelToggles.Single(t => t.Level == LogLevel.Information).Count);
        Assert.StartsWith("4 events  ·  1 error", feed.Vm.StatusText);
    }

    [Fact]
    public void A_continuation_line_replaces_the_open_event_and_the_selection_follows_the_row()
    {
        var feed = new Feed().Send(TestSupport.Line(0, "ERR", "Payment failed"));
        Assert.Null(feed.Vm.Events[0].Exception);

        feed.Send("System.InvalidOperationException: boom\n   at A.B()\n");

        Assert.Single(feed.Vm.Events);
        Assert.Contains("boom", feed.Vm.Events[0].Exception);
    }

    [Fact]
    public void New_events_scroll_to_the_end_while_the_grid_shows_it()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        var scrolls = 0;
        feed.Vm.ScrollToEndRequested += (_, _) => scrolls++;
        feed.Vm.FollowLatest = true;

        feed.Send(TestSupport.Line(2));

        Assert.Equal(1, scrolls);
        Assert.Equal(0, feed.Vm.PendingNewEvents);
    }

    [Fact]
    public void Scrolled_away_new_events_are_counted_until_the_user_jumps_to_them()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        var scrolls = 0;
        feed.Vm.ScrollToEndRequested += (_, _) => scrolls++;
        feed.Vm.FollowLatest = false;

        feed.Send(TestSupport.Lines(2, 4));

        Assert.Equal(0, scrolls);
        Assert.Equal(3, feed.Vm.PendingNewEvents);
        Assert.True(feed.Vm.HasPendingNewEvents);
        Assert.Equal("3 new events — jump to latest", feed.Vm.PendingNewEventsLabel);

        feed.Vm.JumpToLatest();

        Assert.Equal(1, scrolls);
        Assert.Equal(0, feed.Vm.PendingNewEvents);
        Assert.True(feed.Vm.FollowLatest);
    }

    [Fact]
    public void Batches_wait_while_a_background_pass_reads_the_document()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        feed.Vm.BeginPass();

        feed.Send(TestSupport.Lines(2, 3));
        Assert.Equal(2, feed.Vm.Document.Events.Count); // the document did not change under the pass

        feed.Vm.EndPass();
        Assert.Equal(["event 0", "event 1", "event 2", "event 3"], feed.Messages());
    }

    [Fact]
    public void Pausing_holds_back_new_events_in_order_until_resumed()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        feed.Vm.IsFollowing = true;
        feed.Vm.IsPaused = true;

        feed.Send(TestSupport.Lines(2, 3)).Send(TestSupport.Lines(4, 5));

        Assert.Equal(2, feed.Vm.Events.Count);
        Assert.Equal("Paused — 4 new", feed.Vm.TailStatus);

        feed.Vm.IsPaused = false;

        Assert.Equal(Enumerable.Range(0, 6).Select(i => "event " + i), feed.Messages());
        Assert.Equal("● Live", feed.Vm.TailStatus);
    }

    [Fact]
    public void Switching_tail_off_ends_a_pause()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        feed.Vm.IsFollowing = true;
        feed.Vm.IsPaused = true;
        feed.Send(TestSupport.Line(2));

        feed.Vm.IsFollowing = false;

        Assert.False(feed.Vm.IsPaused);
        Assert.Equal(3, feed.Vm.Events.Count);
    }

    // ------------------------------------------------------------------ filters and search (FR-T4)

    [Fact]
    public async Task Active_filters_apply_to_incoming_events()
    {
        var feed = new Feed().Send(TestSupport.Line(0) + TestSupport.Line(1, "ERR"));
        feed.Vm.ShowOnlyLevel(LogLevel.Error);
        await feed.Vm.WhenSettledAsync();
        Assert.Equal(["event 1"], feed.Messages());

        feed.Send(TestSupport.Line(2) + TestSupport.Line(3, "ERR") + TestSupport.Line(4, "ERR"));

        Assert.Equal(["event 1", "event 3", "event 4"], feed.Messages());
        Assert.StartsWith("3 of 5 events", feed.Vm.StatusText);
    }

    [Fact]
    public async Task Find_matches_are_counted_in_new_events_too()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 3));
        feed.Vm.FindBarVisible = true;
        feed.Vm.SearchText = "event 1";
        await feed.Vm.WhenSettledAsync();
        Assert.Equal(1, feed.Vm.MatchCount);

        feed.Send(TestSupport.Lines(4, 12)); // "event 10", "event 11", "event 12" match

        Assert.Equal(4, feed.Vm.MatchCount);
        Assert.Equal("4 matches", feed.Vm.MatchSummary);
    }

    [Fact]
    public void Kinds_of_event_without_a_level_get_a_button_when_they_first_appear()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        Assert.False(feed.Vm.CanFilterByLevel(LogLevel.Unknown));

        feed.Send("2026-11-13 08:00:09.000 +05:30 [XYZ] odd level\n" + TestSupport.Line(10));

        Assert.True(feed.Vm.CanFilterByLevel(LogLevel.Unknown));
        Assert.Equal(1, feed.Vm.LevelToggles.Single(t => t.Level == LogLevel.Unknown).Count);
    }

    // ------------------------------------------------------------------ resets and rolling files (FR-T3)

    [Fact]
    public void A_reset_empties_the_list_and_says_why()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 3));
        feed.Vm.SelectedEvent = feed.Vm.Events[1];
        Notice? notice = null;
        feed.Vm.NoticeRaised += (_, n) => notice = n;
        var resets = 0;
        feed.Vm.RowsReset += (_, _) => resets++;

        feed.Vm.HandleBatch(new ResetBatch("The file got shorter, so it was read again from the start."));
        feed.Restart();

        Assert.Empty(feed.Vm.Events);
        Assert.Null(feed.Vm.SelectedEvent);
        Assert.Equal(1, resets);
        Assert.True(feed.Vm.IsLoading);
        Assert.Equal("log20261113.txt was read again", notice?.Title);

        feed.Send(TestSupport.Lines(100, 101), initialLoadComplete: true);
        Assert.Equal(["event 100", "event 101"], feed.Messages());
        Assert.False(feed.Vm.IsLoading);
    }

    [Fact]
    public void A_rolled_log_continues_in_its_next_file()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));
        Notice? notice = null;
        feed.Vm.NoticeRaised += (_, n) => notice = n;

        feed.Vm.HandleBatch(new SwitchedBatch(@"C:\logs\log20261114.txt"));

        Assert.Equal("log20261114.txt", feed.Vm.Document.FileName);
        Assert.Equal("Following the log", notice?.Title);
        Assert.Contains("log20261114.txt", notice?.Message);
    }

    // ------------------------------------------------------------------ the stream itself

    [Fact]
    public Task The_first_read_shows_progress_and_its_events_are_not_new() => UiThread.Run(async () =>
    {
        var stream = new FakeStream(Path);
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText)) { FollowLatest = false };
        var loaded = 0;
        vm.LoadCompleted += (_, _) => loaded++;
        var run = vm.RunAsync(stream);

        Assert.True(vm.IsLoading);
        Assert.StartsWith("Loading 0", vm.TailStatus);

        stream.Write(TestSupport.Lines(0, 4), fileLength: 400);
        await Eventually.True(() => vm.Events.Count > 0, "the first rows");
        Assert.True(vm.LoadProgress is > 0 and < 1);
        Assert.False(vm.CanRefresh);

        stream.Write(TestSupport.Lines(5, 9), initialLoadComplete: true, fileLength: 400);
        await Eventually.True(() => !vm.IsLoading, "the end of the first read");

        Assert.Equal(1, loaded);
        Assert.Equal(1, vm.LoadProgress);
        Assert.Equal(0, vm.PendingNewEvents); // the file's existing content is not "new"
        Assert.NotNull(vm.FirstRowsAfter);
        Assert.True(vm.CanRefresh);

        vm.Dispose();
        await run;
        Assert.True(vm.IsStopped);
        Assert.True(stream.Disposed);
    });

    [Fact]
    public Task Tail_and_refresh_reach_the_stream() => UiThread.Run(async () =>
    {
        var stream = new FakeStream(Path);
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText));
        _ = vm.RunAsync(stream);
        stream.Load(TestSupport.Lines(0, 1));
        await Eventually.True(() => !vm.IsLoading, "the first read");

        vm.Refresh();
        Assert.Equal(1, stream.Refreshes);

        vm.IsFollowing = true;
        Assert.True(stream.Follow);
        Assert.False(vm.CanRefresh); // nothing to refresh while tailing
        vm.Refresh();
        Assert.Equal(1, stream.Refreshes);

        vm.FollowRolled = false;
        Assert.False(stream.FollowRolled);
        vm.Dispose();
    });

    [Fact]
    public Task Tail_on_from_the_start_shows_the_end_once_the_file_is_read() => UiThread.Run(async () =>
    {
        var stream = new FakeStream(Path);
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText)) { IsFollowing = true };
        var scrolls = 0;
        vm.ScrollToEndRequested += (_, _) => scrolls++;
        _ = vm.RunAsync(stream);
        Assert.True(stream.Follow);

        stream.Load(TestSupport.Lines(0, 50));
        await Eventually.True(() => !vm.IsLoading, "the first read");

        Assert.Equal(1, scrolls);
        vm.Dispose();
    });

    [Fact]
    public Task Stopping_the_load_keeps_what_was_read() => UiThread.Run(async () =>
    {
        var stream = new FakeStream(Path);
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText));
        var run = vm.RunAsync(stream);
        stream.Write(TestSupport.Lines(0, 4), fileLength: 1000);
        await Eventually.True(() => vm.Events.Count > 0, "the first rows");

        vm.StopLoading();
        await run;

        Assert.False(vm.IsLoading);
        Assert.True(vm.IsStopped);
        Assert.False(vm.CanFollow);
        Assert.False(vm.CanRefresh);
        Assert.True(vm.Events.Count > 0);
    });

    [Fact]
    public Task A_file_that_cannot_be_read_is_reported() => UiThread.Run(async () =>
    {
        var stream = new FakeStream(Path);
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText));
        Exception? failure = null;
        vm.StreamFailed += (_, ex) => failure = ex;
        var run = vm.RunAsync(stream);

        stream.Fail(new FileNotFoundException("gone", Path));
        await run;

        Assert.IsType<FileNotFoundException>(failure);
        Assert.True(vm.IsStopped);
    });

    [Fact]
    public void Waiting_for_a_file_that_went_away_is_shown()
    {
        var feed = new Feed().Send(TestSupport.Lines(0, 1));

        feed.Vm.HandleBatch(new StatusBatch(StreamState.WaitingForFile));

        Assert.Equal("Waiting for log20261113.txt…", feed.Vm.TailStatus);
    }
}
