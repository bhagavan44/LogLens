using LogLens.Core.Formats;
using LogLens.Core.Merging;
using LogLens.Core.Models;
using LogLens.Core.State;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels.Tests;

/// <summary>Bookmarks (FR-N1) and "find all events containing" (FR-N4) in <see cref="LogViewModel"/>.</summary>
public class BookmarkTests
{
    private const string PathA = @"C:\logs\a.txt";
    private const string PathB = @"C:\logs\b.txt";

    private static LogViewModel Vm(BookmarkService bookmarks, string text = Docs.Sample, string path = PathA)
        => new(TestSupport.Document(text, path), bookmarks);

    private static LogEvent Find(LogViewModel vm, string message) => vm.Document.Events.First(e => e.Message == message);

    [Fact]
    public void Toggling_bookmarks_an_event_and_remembers_it_by_file_offset_and_time()
    {
        var store = new MemoryStateStore();
        var vm = Vm(new BookmarkService(store));
        var e = Find(vm, "Retry for payment");
        var changes = 0;
        vm.BookmarksChanged += (_, _) => changes++;

        Assert.True(vm.ToggleBookmark(e));

        Assert.True(vm.IsBookmarked(e));
        Assert.True(vm.HasBookmarks);
        var item = Assert.Single(vm.Bookmarks);
        Assert.Same(e, item.Event);
        Assert.Equal("#2", item.NumberText);
        Assert.Equal("WRN", item.LevelText);
        var saved = Assert.Single(store.State.Bookmarks);
        Assert.Equal(PathA, saved.File);
        Assert.Equal(e.Offset, saved.Offset);
        Assert.Equal(e.Timestamp, saved.Timestamp);
        Assert.Equal("Retry for payment", saved.Preview);
        Assert.Equal(1, changes);

        Assert.False(vm.ToggleBookmark(e));
        Assert.False(vm.IsBookmarked(e));
        Assert.Empty(vm.Bookmarks);
        Assert.Empty(store.State.Bookmarks);
    }

    [Fact]
    public void Bookmarks_are_found_again_when_the_file_is_opened_again()
    {
        var store = new MemoryStateStore();
        var first = Vm(new BookmarkService(store));
        first.ToggleBookmark(Find(first, "Order 3 shipped"));
        first.Dispose();

        var again = Vm(new BookmarkService(store));

        Assert.Equal("Order 3 shipped", Assert.Single(again.Bookmarks).Event.Message);
    }

    [Fact]
    public void A_bookmark_whose_event_changed_is_not_put_on_another_event()
    {
        var store = new MemoryStateStore();
        var first = Vm(new BookmarkService(store));
        first.ToggleBookmark(Find(first, "Retry for payment"));

        // The file was replaced: the same offsets now hold events of another time.
        var replaced = Vm(new BookmarkService(store), Docs.Sample.Replace("08:0", "09:0", StringComparison.Ordinal));

        Assert.Empty(replaced.Bookmarks);
    }

    [Fact]
    public void A_bookmark_set_in_one_tab_shows_in_every_tab_of_that_file()
    {
        var service = new BookmarkService(new MemoryStateStore());
        var one = Vm(service);
        var two = Vm(service);
        var other = Vm(service, path: PathB);

        one.ToggleBookmark(Find(one, "cache hit"));

        Assert.Single(two.Bookmarks);
        Assert.Empty(other.Bookmarks);

        two.Dispose();
        one.ToggleBookmark(Find(one, "cache hit"));
        Assert.Single(two.Bookmarks); // a closed tab no longer listens
    }

    [Fact]
    public async Task F2_steps_through_the_bookmarks_the_filters_leave_wrapping_around()
    {
        var vm = Vm(new BookmarkService(new MemoryStateStore()));
        foreach (var message in new[] { "Order 1 shipped", "cache hit", "Shutting down" })
        {
            vm.ToggleBookmark(Find(vm, message));
        }

        Assert.Equal(3, vm.NextBookmarkRow(0, forward: true));
        Assert.Equal(5, vm.NextBookmarkRow(3, forward: true));
        Assert.Equal(0, vm.NextBookmarkRow(5, forward: true));
        Assert.Equal(5, vm.NextBookmarkRow(0, forward: false));

        vm.LevelToggles.Single(t => t.Level == LogLevel.Debug).IsOn = false;
        await vm.WhenSettledAsync();

        Assert.Equal(4, vm.NextBookmarkRow(0, forward: true)); // "cache hit" is hidden; "Shutting down" is now row 4
        Assert.Equal(-1, vm.RowOf(vm.Bookmarks.Single(b => b.Event.Message == "cache hit")));
    }

    [Fact]
    public void Notes_are_saved_without_rebuilding_the_list()
    {
        var store = new MemoryStateStore();
        var vm = Vm(new BookmarkService(store));
        vm.ToggleBookmark(Find(vm, "Order 1 shipped"));
        var item = vm.Bookmarks[0];
        var changes = 0;
        vm.BookmarksChanged += (_, _) => changes++;

        item.Note = "first order";

        Assert.Equal("first order", store.State.Bookmarks[0].Note);
        Assert.Same(item, vm.Bookmarks[0]);
        Assert.Equal(0, changes);
        Assert.Contains("note: first order", item.AccessibleName);
    }

    [Fact]
    public void Clearing_removes_the_bookmarks_of_this_log_only()
    {
        var store = new MemoryStateStore();
        var service = new BookmarkService(store);
        var a = Vm(service);
        var b = Vm(service, path: PathB);
        a.ToggleBookmark(Find(a, "Order 1 shipped"));
        a.ToggleBookmark(Find(a, "cache hit"));
        b.ToggleBookmark(Find(b, "cache hit"));

        a.ClearBookmarks();

        Assert.Empty(a.Bookmarks);
        Assert.Single(b.Bookmarks);
        Assert.Equal(PathB, Assert.Single(store.State.Bookmarks).File);
    }

    [Fact]
    public void Bookmarks_carry_over_between_a_file_and_a_merged_view_of_it()
    {
        var service = new BookmarkService(new MemoryStateStore());
        var b = Vm(service, TestSupport.Line(5, message: "in b"), PathB);
        b.ToggleBookmark(Find(b, "in b"));

        var merged = new LogViewModel(Merged(TestSupport.Line(0) + TestSupport.Line(10), TestSupport.Line(5, message: "in b")), service);

        var item = Assert.Single(merged.Bookmarks);
        Assert.Equal("in b", item.Event.Message);
        Assert.Equal("b.txt", item.Source);
        Assert.True(merged.IsMerged);

        // ... and the other way round.
        merged.ToggleBookmark(merged.Document.Events.First(e => e.Message == "event 10"));
        var a = Vm(service, TestSupport.Line(0) + TestSupport.Line(10));
        Assert.Equal("event 10", Assert.Single(a.Bookmarks).Event.Message);
    }

    [Fact]
    public void A_bookmark_shows_once_its_event_arrives_while_loading()
    {
        var service = new BookmarkService(new MemoryStateStore());
        var complete = Vm(service, TestSupport.Lines(0, 9));
        complete.ToggleBookmark(Find(complete, "event 8"));

        var parser = LogFormats.SerilogText.CreateParser(PathA);
        var loading = new LogViewModel(new LogDocument(PathA, LogFormats.SerilogText), service);
        var first = TestSupport.Lines(0, 4);
        loading.HandleBatch(new ChunkBatch(first, parser.Append(first), 1, 2, false, PathA, "UTF-8"));
        Assert.Empty(loading.Bookmarks);

        var rest = TestSupport.Lines(5, 9);
        loading.HandleBatch(new ChunkBatch(rest, parser.Append(rest), 2, 2, true, PathA, "UTF-8"));
        Assert.Equal("event 8", Assert.Single(loading.Bookmarks).Event.Message);
    }

    // ------------------------------------------------------------------ find all events containing (FR-N4)

    [Fact]
    public async Task Find_all_lists_only_the_events_containing_the_text_with_context_and_clears_other_filters()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l = 'Information'";
        vm.MatchCase = true;
        await vm.WhenSettledAsync();

        vm.FindAllContaining("  ORDER 2 ", contextEvents: 1);
        await vm.WhenSettledAsync();

        Assert.Equal(["Retry for payment", "Payment failed for order 2", "cache hit"], vm.View.Select(e => e.Message));
        Assert.Equal(string.Empty, vm.QueryText);
        Assert.True(vm.FindBarVisible);
        Assert.True(vm.OnlyMatches);
        Assert.False(vm.MatchCase);
        Assert.Equal("ORDER 2", vm.SearchText);
        Assert.Equal(1, vm.MatchCount);
    }

    [Fact]
    public async Task Find_all_can_keep_the_other_filters_and_also_searches_exceptions()
    {
        var vm = Docs.Vm();
        vm.LevelToggles.Single(t => t.Level == LogLevel.Error).IsOn = false;
        await vm.WhenSettledAsync();

        vm.FindAllContaining("Shop.Pay", clearOtherFilters: false);
        await vm.WhenSettledAsync();

        Assert.Empty(vm.View); // the only match is an error, and errors stay hidden
        Assert.False(vm.LevelToggles.Single(t => t.Level == LogLevel.Error).IsOn);

        vm.FindAllContaining("Shop.Pay");
        await vm.WhenSettledAsync();
        Assert.Equal("Payment failed for order 2", Assert.Single(vm.View).Message);
    }

    [Fact]
    public async Task Find_all_in_a_merged_view_covers_every_file()
    {
        var vm = new LogViewModel(Merged(TestSupport.Line(0, message: "request 7 start") + TestSupport.Line(10), TestSupport.Line(5, message: "request 7 done")));

        vm.FindAllContaining("request 7");
        await vm.WhenSettledAsync();

        Assert.Equal([PathA, PathB], vm.View.Select(e => e.SourceFile));
    }

    [Fact]
    public void Find_all_with_blank_text_does_nothing()
    {
        var vm = Docs.Vm();

        vm.FindAllContaining("   ");

        Assert.False(vm.FindBarVisible);
    }

    private static LogDocument Merged(string a, string b)
    {
        var merged = LogMerger.Merge([TestSupport.Document(a, PathA), TestSupport.Document(b, PathB)]);
        var document = new LogDocument(null, LogFormats.SerilogText);
        document.AddMergedFiles(merged.Files);
        document.Text.Append(merged.Text);
        document.Apply(new ParseDelta(0, merged.Events));
        return document;
    }
}
