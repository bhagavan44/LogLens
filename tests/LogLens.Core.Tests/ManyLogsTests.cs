using LogLens.Core.Formats;
using LogLens.Core.Merging;
using LogLens.Core.Models;
using LogLens.Core.State;

namespace LogLens.Core.Tests;

/// <summary>Merge view (FR-O4), bookmarks (FR-N1) and tabs in the session (FR-P3).</summary>
public class LogMergerTests
{
    private static string Line(string time, string message, string offset = "+05:30", string level = "INF")
        => $"2026-11-13 {time}.000 {offset} [{level}] {message}\n";

    private static LogDocument Doc(string text, string path) => TestDocuments.FromText(text, path);

    [Fact]
    public void Events_of_several_files_come_out_in_time_order_with_their_file()
    {
        var a = Doc(Line("08:00:00", "a1") + Line("08:00:10", "a2"), @"C:\a.txt");
        var b = Doc(Line("08:00:05", "b1") + Line("08:00:15", "b2"), @"C:\b.txt");

        var merged = LogMerger.Merge([a, b]);

        Assert.Equal(["a1", "b1", "a2", "b2"], merged.Events.Select(e => e.Message));
        Assert.Equal([@"C:\a.txt", @"C:\b.txt", @"C:\a.txt", @"C:\b.txt"], merged.Events.Select(e => e.SourceFile));
        Assert.Equal([0, 1, 2, 3], merged.Events.Select(e => e.Id));
    }

    [Fact]
    public void Offsets_point_into_the_merged_text_so_raw_lines_are_exact()
    {
        var a = Doc(Line("08:00:00", "a1"), @"C:\a.txt");
        var b = Doc(Line("07:00:00", "b1") + "a continuation line\n", @"C:\b.txt");

        var merged = LogMerger.Merge([a, b]);

        Assert.Equal([new FileSegment(0, @"C:\a.txt"), new FileSegment(a.Text.Length, @"C:\b.txt")], merged.Files);
        var first = merged.Events[0];
        Assert.Equal("b1\na continuation line", first.Message);
        Assert.Equal(b.GetRaw(b.Events[0]), merged.Text.Substring((int)first.Offset, first.Length));
        Assert.Equal(a.GetRaw(a.Events[0]), merged.Text.Substring((int)merged.Events[1].Offset, merged.Events[1].Length));
    }

    [Fact]
    public void Files_logged_in_different_offsets_interleave_by_instant()
    {
        var india = Doc(Line("13:30:00", "india noon-ish") + Line("13:30:20", "india later"), "in.txt");
        var utc = Doc(Line("08:00:10", "utc between", "+00:00"), "utc.txt");

        var merged = LogMerger.Merge([india, utc]);

        Assert.Equal(["india noon-ish", "utc between", "india later"], merged.Events.Select(e => e.Message));
    }

    [Fact]
    public void Each_file_keeps_its_order_and_unparsed_lines_stay_with_the_event_before_them()
    {
        var a = Doc(Line("08:00:10", "a1") + Line("08:00:00", "clock went back"), "a.txt");
        var b = Doc("garbage first\n" + Line("08:00:05", "b1"), "b.txt");

        var merged = LogMerger.Merge([a, b]);

        Assert.Equal(["garbage first", "b1", "a1", "clock went back"], merged.Events.Select(e => e.Message));
    }

    [Fact]
    public void Ties_go_to_the_file_given_first()
    {
        var a = Doc(Line("08:00:00", "a"), "a.txt");
        var b = Doc(Line("08:00:00", "b"), "b.txt");

        Assert.Equal(["b", "a"], LogMerger.Merge([b, a]).Events.Select(e => e.Message));
    }

    [Fact]
    public void A_file_without_a_final_newline_does_not_join_the_next_files_first_line()
    {
        var a = Doc(Line("08:00:00", "a").TrimEnd('\n'), "a.txt");
        var b = Doc(Line("08:00:01", "b"), "b.txt");

        var merged = LogMerger.Merge([a, b]);

        Assert.Equal(a.Text.Length + 1, merged.Files[1].Start);
        Assert.Equal('\n', merged.Text[(int)merged.Files[1].Start - 1]);
    }

    [Fact]
    public void Empty_files_merge_and_nothing_at_all_is_refused()
    {
        var merged = LogMerger.Merge([Doc(string.Empty, "a.txt"), Doc(Line("08:00:00", "b"), "b.txt")]);
        Assert.Equal(["b"], merged.Events.Select(e => e.Message));
        Assert.Equal(0, merged.Files[1].Start);

        Assert.Throws<ArgumentException>(() => LogMerger.Merge([]));
    }
}

public class MergedDocumentTests
{
    [Fact]
    public void A_merged_document_counts_lines_per_file_and_knows_each_events_offset_in_its_file()
    {
        var a = TestDocuments.FromText("2026-11-13 08:00:00.000 +00:00 [INF] a1\n2026-11-13 08:00:10.000 +00:00 [INF] a2\n", @"C:\a.txt");
        var b = TestDocuments.FromText("2026-11-13 08:00:05.000 +00:00 [INF] b1\n", @"C:\b.txt");
        var merged = LogMerger.Merge([a, b]);

        var document = new LogDocument(null, LogFormats.SerilogText);
        document.AddMergedFiles(merged.Files);
        document.Text.Append(merged.Text);
        document.Apply(new ParseDelta(0, merged.Events));

        Assert.True(document.IsMerged);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], document.MergedFiles);
        Assert.Equal("a.txt + 1 more", document.FileName);
        var b1 = document.Events[1];
        Assert.Equal(1, document.LineNumberOf(b1));
        Assert.Equal(2, document.LineNumberOf(document.Events[2]));
        Assert.Equal(b.Events[0].Offset, document.OriginalOffset(b1));
        Assert.Equal(a.Events[1].Offset, document.OriginalOffset(document.Events[2]));
    }

    [Fact]
    public void A_single_file_is_not_merged_and_offsets_are_its_own()
    {
        var document = TestDocuments.FromText("2026-11-13 08:00:00.000 +00:00 [INF] a\n2026-11-13 08:00:01.000 +00:00 [INF] b\n", @"C:\a.txt");

        Assert.False(document.IsMerged);
        Assert.Null(document.MergedFiles);
        Assert.Equal("a.txt", document.FileName);
        Assert.Equal(document.Events[1].Offset, document.OriginalOffset(document.Events[1]));
    }

    [Fact]
    public void After_a_rolling_log_moved_on_offsets_count_from_the_new_files_start()
    {
        var document = new LogDocument(@"C:\log1.txt", LogFormats.SerilogText);
        var first = "2026-11-13 08:00:00.000 +00:00 [INF] one\n";
        var parser = LogFormats.SerilogText.CreateParser(@"C:\log1.txt");
        document.Text.Append(first);
        document.Apply(parser.Append(first));
        document.Apply(parser.Complete());

        document.BeginFile(@"C:\log2.txt");
        var second = "2026-11-14 08:00:00.000 +00:00 [INF] two\n";
        var next = LogFormats.SerilogText.CreateParser(@"C:\log2.txt", document.Text.Length, document.Events.Count);
        document.Text.Append(second);
        document.Apply(next.Append(second));
        document.Apply(next.Complete());

        Assert.Equal(0, document.OriginalOffset(document.Events[1]));
    }
}

public class BookmarkServiceTests
{
    private static Bookmark Mark(string file, long offset, string note = "") => new() { File = file, Offset = offset, Preview = "p", Note = note };

    [Fact]
    public void Toggling_adds_then_removes_and_every_change_is_saved()
    {
        var store = new InMemoryStateStore();
        var service = new BookmarkService(store);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        Assert.True(service.Toggle(Mark(@"C:\a.txt", 10)));
        Assert.Single(store.Load().Bookmarks);
        Assert.False(service.Toggle(Mark(@"c:\A.TXT", 10))); // the same event, whatever the case of the path
        Assert.Empty(store.Load().Bookmarks);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Notes_change_without_raising_changed()
    {
        var store = new InMemoryStateStore();
        var service = new BookmarkService(store);
        service.Toggle(Mark("a", 1));
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.SetNote(Mark("a", 1), "look here");
        service.SetNote(Mark("b", 1), "not bookmarked");

        Assert.Equal("look here", Assert.Single(store.Load().Bookmarks).Note);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Clearing_files_and_removing_one_leave_the_rest()
    {
        var service = new BookmarkService(new InMemoryStateStore());
        service.Toggle(Mark("a", 1));
        service.Toggle(Mark("a", 2));
        service.Toggle(Mark("b", 1));
        service.Toggle(Mark("c", 1));

        service.Clear(["A"]);
        service.Remove(Mark("b", 1));

        Assert.Equal(["c"], service.All.Select(b => b.File));
    }

    [Fact]
    public void Loading_skips_entries_without_a_file_and_keeps_only_the_newest_bookmarks()
    {
        var many = Enumerable.Range(0, BookmarkService.MaxBookmarks + 5).Select(i => Mark("a", i)).Prepend(Mark(" ", 0)).ToList();
        var store = new InMemoryStateStore();
        store.Save(new AppState { Bookmarks = many });

        var service = new BookmarkService(store);

        Assert.Equal(BookmarkService.MaxBookmarks, service.Count);
        Assert.Equal(5, service.All[0].Offset);
    }

    [Fact]
    public void A_failed_save_is_reported_and_the_change_still_applies()
    {
        Exception? reported = null;
        var service = new BookmarkService(new FailingStore(), ex => reported = ex);

        service.Toggle(Mark("a", 1));

        Assert.IsType<IOException>(reported);
        Assert.Equal(1, service.Count);
    }

    private sealed class FailingStore : IStateStore
    {
        public AppState Load() => new();

        public void Save(AppState state) => throw new IOException("disk full");
    }
}

public class SessionTabsTests
{
    [Fact]
    public void Merged_tabs_are_kept_and_compared_by_their_files()
    {
        var store = new InMemoryStateStore();
        var service = new SessionService(store);

        service.Save(new SessionState
        {
            Tabs = [new SessionTab { MergedFiles = ["a", "", "b"] }, new SessionTab { MergedFiles = ["only one"] }, new SessionTab { Path = "c" }],
            Selected = 1,
        });

        var saved = store.Load().Session;
        Assert.Equal(2, saved.Tabs.Count);
        Assert.Equal(["a", "b"], saved.Tabs[0].MergedFiles);
        Assert.Equal("c", saved.Tabs[1].Path);
        Assert.Equal(1, saved.Selected);
    }

    [Fact]
    public void A_different_set_of_merged_files_is_a_change()
    {
        var store = new CountingStore();
        var service = new SessionService(store);

        service.Save(new SessionState { Tabs = [new SessionTab { MergedFiles = ["a", "b"] }] });
        service.Save(new SessionState { Tabs = [new SessionTab { MergedFiles = ["A", "B"] }] });
        service.Save(new SessionState { Tabs = [new SessionTab { MergedFiles = ["a", "c"] }] });

        Assert.Equal(2, store.Saves);
    }

    private sealed class CountingStore : IStateStore
    {
        private AppState _state = new();

        public int Saves { get; private set; }

        public AppState Load() => _state;

        public void Save(AppState state)
        {
            Saves++;
            _state = state;
        }
    }
}
