using LogLens.Core.Collections;
using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.State;
using LogLens.Core.Streaming;

namespace LogLens.Core.Tests;

public class RollPatternTests
{
    [Theory]
    [InlineData("log20261113.txt", "log*.txt")]
    [InlineData("viewer-20261113_001.txt", "viewer-*.txt")]
    [InlineData("api2.log", "api*.log")]
    [InlineData("app.log", null)]
    [InlineData("noext1", "noext*")]
    [InlineData("live2-20261113.txt", "live2-*.txt")] // the "2" is part of the name; the date stamp is the long run
    [InlineData("log-2026-11-13.txt", "log-*.txt")] // no 6-digit run: the first digit
    [InlineData("app-001.log", "app-*.log")]
    [InlineData("svc7-api-2026111309.txt", "svc7-api-*.txt")]
    public void The_pattern_is_the_name_before_the_date_stamp_plus_the_extension(string name, string? expected)
        => Assert.Equal(expected, RollPattern.FromFileName(name));

    [Theory]
    [InlineData("log*.txt", "log20261114.txt", true)]
    [InlineData("log*.txt", "LOG20261114.TXT", true)]
    [InlineData("log*.txt", "other20261114.txt", false)]
    [InlineData("log*.txt", "log20261114.log", false)]
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "abbc.txt", false)]
    public void Glob_matching(string pattern, string name, bool expected)
        => Assert.Equal(expected, RollPattern.Matches(pattern, name));

    private static readonly DateTime T0 = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_greatest_later_name_wins()
    {
        var next = RollPattern.PickSuccessor("log*.txt", "log20261113.txt", T0,
        [
            new RollCandidate("log20261113.txt", T0),
            new RollCandidate("log20261112.txt", T0.AddDays(-1)),
            new RollCandidate("log20261115.txt", T0.AddDays(2)),
            new RollCandidate("log20261114.txt", T0.AddDays(1)),
            new RollCandidate("other.txt", T0.AddDays(5)),
        ]);

        Assert.Equal("log20261115.txt", next);
    }

    [Fact]
    public void Size_rolled_suffixes_sort_after_their_base_name()
        => Assert.Equal("log20261113_001.txt", RollPattern.PickSuccessor("log*.txt", "log20261113.txt", T0, [new RollCandidate("log20261113_001.txt", T0.AddHours(1))]));

    [Fact]
    public void Without_a_later_name_a_newer_file_wins_and_otherwise_the_log_stays()
    {
        Assert.Equal("log-a.txt", RollPattern.PickSuccessor("log*.txt", "log-b.txt", T0, [new RollCandidate("log-a.txt", T0.AddMinutes(5))]));
        Assert.Null(RollPattern.PickSuccessor("log*.txt", "log-b.txt", T0, [new RollCandidate("log-a.txt", T0.AddMinutes(-5))]));
        Assert.Null(RollPattern.PickSuccessor("log*.txt", "log-b.txt", T0, []));
    }
}

public class IncrementalParserTests
{
    private const string Text = "2026-11-13 08:00:00.000 +00:00 [INF] one\n2026-11-13 08:00:01.000 +00:00 [ERR] two\nSystem.Exception: x\n   at A.B()\n";

    [Fact]
    public void Preview_shows_an_unterminated_last_line_without_consuming_it()
    {
        var parser = LogFormats.SerilogText.CreateParser("a.txt");
        parser.Append("2026-11-13 08:00:00.000 +00:00 [INF] one\n2026-11-13 08:00:01.000 +00:00 [INF] tw");
        Assert.True(parser.HasPartialLine);

        var preview = parser.Preview();
        Assert.Equal(["one", "tw"], preview.Events.Select(e => e.Message));
        Assert.Equal(0, preview.FirstId);

        var after = parser.Append("o\n");
        Assert.Equal("two", after.Events[^1].Message); // the preview changed nothing
        Assert.False(parser.HasPartialLine);
    }

    [Fact]
    public void A_parser_that_continues_a_document_starts_at_the_given_offset_and_id()
    {
        var first = LogFormats.SerilogText.CreateParser("day1.txt");
        first.Append(Text);
        var closing = first.Complete();
        Assert.Equal(2, first.CommittedId);
        Assert.Equal(Text.Length, first.EndOffset);

        var next = LogFormats.SerilogText.CreateParser("day2.txt", first.EndOffset, first.CommittedId);
        var delta = next.Append("2026-11-14 00:00:00.000 +00:00 [INF] three\n");
        var final = next.Complete(); // replaces the provisional event of the first delta

        Assert.Equal(2, delta.FirstId);
        Assert.Equal(2, final.FirstId);
        var three = Assert.Single(final.Events);
        Assert.Equal(2, three.Id);
        Assert.Equal(Text.Length, three.Offset);
        Assert.Equal("day2.txt", three.SourceFile);
        Assert.NotNull(closing);
    }
}

public class GrowingDocumentTests
{
    private const string Day1 = "2026-11-13 08:00:00.000 +00:00 [INF] one\n2026-11-13 08:00:01.000 +00:00 [WRN] two\n";
    private const string Day2 = "2026-11-14 00:00:00.000 +00:00 [INF] three\n2026-11-14 00:00:01.000 +00:00 [ERR] four\n";

    private static (LogDocument Document, ILogParser Parser) Open(string text)
    {
        var document = new LogDocument("day1.txt", LogFormats.SerilogText);
        var parser = LogFormats.SerilogText.CreateParser("day1.txt");
        document.Text.Append(text);
        document.Apply(parser.Append(text));
        return (document, parser);
    }

    [Fact]
    public void Apply_says_what_changed()
    {
        var document = new LogDocument("a.txt", LogFormats.SerilogText);
        var parser = LogFormats.SerilogText.CreateParser("a.txt");
        document.Text.Append(Day1);

        Assert.Equal(new DocumentChange(0, 0, 2), document.Apply(parser.Append(Day1)));

        // The open last event is provisional: the closing delta replaces it.
        Assert.Equal(new DocumentChange(1, 1, 1), document.Apply(parser.Complete()));
    }

    [Fact]
    public void A_rolled_file_continues_the_document_with_its_own_line_numbers()
    {
        var (document, parser) = Open(Day1);
        document.Apply(parser.Complete());

        document.BeginFile("day2.txt");
        var next = LogFormats.SerilogText.CreateParser("day2.txt", parser.EndOffset, parser.CommittedId);
        document.Text.Append(Day2);
        document.Apply(next.Append(Day2));
        document.Apply(next.Complete());

        Assert.Equal(4, document.Events.Count);
        Assert.Equal("day2.txt", document.CurrentFilePath);
        Assert.Equal("day2.txt", document.FileName);
        Assert.Equal("day1.txt", document.FilePath);
        Assert.Equal([1, 2, 1, 2], document.Events.Select(document.LineNumberOf));
        Assert.Equal("2026-11-14 00:00:01.000 +00:00 [ERR] four", document.GetRaw(document.Events[3]));
    }

    [Fact]
    public void Clear_forgets_everything_but_stays_on_the_current_file()
    {
        var (document, parser) = Open(Day1);
        document.Apply(parser.Complete());
        document.BeginFile("day2.txt");
        document.LoadTime = TimeSpan.FromSeconds(1);

        document.Clear();

        Assert.Empty(document.Events);
        Assert.Equal(0, document.Text.Length);
        Assert.Equal(0, document.CountOf(LogLevel.Warning));
        Assert.Equal("day2.txt", document.CurrentFilePath);
        Assert.Equal(TimeSpan.Zero, document.LoadTime);
    }

    [Fact]
    public void The_line_index_grows_with_the_text()
    {
        var store = new TextStore();
        store.Append("a\nb\n");
        Assert.Equal(3, store.LineNumberAt(4));

        store.Append("c\nd"); // merged into the same chunk
        Assert.Equal(3, store.LineNumberAt(4));
        Assert.Equal(4, store.LineNumberAt(6));

        store.Append(new string('x', 300_000) + "\ne"); // a new chunk
        Assert.Equal(5, store.LineNumberAt(store.Length - 1));

        store.Clear();
        store.Append("z");
        Assert.Equal(1, store.LineNumberAt(0));
    }
}

public class LogEventViewSyncTests
{
    private static LogEvent Event(int id, LogLevel level = LogLevel.Information)
        => new(id, null, id * 10, 5, DateTimeOffset.UnixEpoch.AddSeconds(id), level, "m" + id, null);

    [Fact]
    public void Unfiltered_appends_add_rows_at_the_end()
    {
        var list = new LogEventList();
        list.Add(Event(0));
        var view = new LogEventView(list);

        list.Add(Event(1));
        list.Add(Event(2));

        Assert.Equal((1, 0, 2), view.Sync(null, 1));
        Assert.Equal(3, view.Count);
    }

    [Fact]
    public void A_replaced_last_event_is_one_row_removed_and_one_added()
    {
        var list = new LogEventList();
        list.Add(Event(0));
        list.Add(Event(1));
        var view = new LogEventView(list);

        list.TruncateTo(1);
        list.Add(Event(1, LogLevel.Error));

        Assert.Equal((1, 1, 1), view.Sync(null, 1));
        Assert.Equal(LogLevel.Error, view[1].Level);
    }

    [Fact]
    public void Filtered_only_the_new_events_are_tested()
    {
        var list = new LogEventList();
        list.Add(Event(0, LogLevel.Error));
        list.Add(Event(1));
        var view = new LogEventView(list);
        bool IsError(LogEvent e) => e.Level == LogLevel.Error;
        view.SetFilter([0]);

        list.Add(Event(2, LogLevel.Error));
        list.Add(Event(3));
        list.Add(Event(4, LogLevel.Error));

        Assert.Equal((1, 0, 2), view.Sync(IsError, 2));
        Assert.Equal([0, 2, 4], view.Select(e => e.Id));
        Assert.Equal(2, view.IndexOf(list[4]));
        Assert.Equal(-1, view.IndexOf(list[3]));
        Assert.Equal([1, 2], view.FindRows(e => e.Id >= 2));
    }

    [Fact]
    public void Filtered_a_replaced_match_is_dropped_and_tested_again()
    {
        var list = new LogEventList();
        list.Add(Event(0, LogLevel.Error));
        list.Add(Event(1, LogLevel.Error));
        var view = new LogEventView(list);
        view.SetFilter([0, 1]);

        list.TruncateTo(1);
        list.Add(Event(1, LogLevel.Information)); // the provisional event turned out not to match

        Assert.Equal((1, 1, 0), view.Sync(e => e.Level == LogLevel.Error, 1));
        Assert.Equal([0], view.Select(e => e.Id));
    }

    [Fact]
    public void A_cleared_source_empties_the_view()
    {
        var list = new LogEventList();
        list.Add(Event(0));
        list.Add(Event(1));
        var view = new LogEventView(list);

        list.Clear();

        Assert.Equal((0, 2, 0), view.Sync(null, 0));
        Assert.Empty(view);
    }
}

/// <summary>An <see cref="IStateStore"/> in memory, optionally failing on save.</summary>
internal sealed class MemoryStateStore : IStateStore
{
    public AppState State { get; set; } = new();

    public int Saves { get; private set; }

    public bool FailSaves { get; set; }

    public AppState Load() => State;

    public void Save(AppState state)
    {
        if (FailSaves)
        {
            throw new IOException("disk full");
        }

        Saves++;
        State = state;
    }
}

public class RecentFilesServiceTests
{
    [Fact]
    public void Newest_first_without_duplicates_ignoring_case_and_capped()
    {
        var store = new MemoryStateStore();
        var recent = new RecentFilesService(store, max: 3);

        recent.Add(@"C:\a.txt");
        recent.Add(@"C:\b.txt");
        recent.Add(@"C:\A.TXT");
        recent.Add(@"C:\c.txt");
        recent.Add(@"C:\d.txt");

        Assert.Equal([@"C:\d.txt", @"C:\c.txt", @"C:\A.TXT"], recent.Files);
        Assert.Equal(recent.Files, store.State.RecentFiles);
    }

    [Fact]
    public void Loads_what_was_saved_and_drops_blank_entries()
    {
        var store = new MemoryStateStore { State = new AppState { RecentFiles = [@"C:\a.txt", " ", @"C:\A.txt", @"C:\b.txt"] } };

        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], new RecentFilesService(store).Files);
    }

    [Fact]
    public void Remove_and_clear()
    {
        var recent = new RecentFilesService(new MemoryStateStore());
        recent.Add(@"C:\a.txt");
        recent.Add(@"C:\b.txt");
        var changes = 0;
        recent.Changed += (_, _) => changes++;

        recent.Remove(@"C:\A.TXT");
        Assert.Equal([@"C:\b.txt"], recent.Files);

        recent.Clear();
        Assert.Empty(recent.Files);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Re_adding_the_newest_file_does_not_touch_the_disk()
    {
        var store = new MemoryStateStore();
        var recent = new RecentFilesService(store);
        recent.Add(@"C:\a.txt");

        recent.Add(@"C:\a.txt");

        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void A_failed_save_is_reported_and_the_list_still_works()
    {
        Exception? reported = null;
        var recent = new RecentFilesService(new MemoryStateStore { FailSaves = true }, onSaveError: ex => reported = ex);

        recent.Add(@"C:\a.txt");

        Assert.IsType<IOException>(reported);
        Assert.Equal([@"C:\a.txt"], recent.Files);
    }
}

public class SessionServiceTests
{
    [Fact]
    public void Saves_the_open_logs_and_keeps_the_recent_files()
    {
        var store = new MemoryStateStore { State = new AppState { RecentFiles = [@"C:\r.txt"] } };
        var session = new SessionService(store);

        session.Save(new SessionState { Tabs = [new SessionTab { Path = @"C:\a.txt", Follow = true }] });

        Assert.Equal(@"C:\a.txt", store.State.Session.Tabs.Single().Path);
        Assert.True(store.State.Session.Tabs.Single().Follow);
        Assert.Equal([@"C:\r.txt"], store.State.RecentFiles);
    }

    [Fact]
    public void Invalid_entries_are_dropped_and_the_selection_is_clamped()
    {
        var store = new MemoryStateStore
        {
            State = new AppState { Session = new SessionState { Tabs = [new SessionTab { Path = " " }, new SessionTab { Path = @"C:\a.txt" }], Selected = 7 } },
        };

        var current = new SessionService(store).Current;

        Assert.Equal(@"C:\a.txt", current.Tabs.Single().Path);
        Assert.Equal(0, current.Selected);
    }

    [Fact]
    public void An_unchanged_session_is_not_saved_again()
    {
        var store = new MemoryStateStore();
        var session = new SessionService(store);
        var state = new SessionState { Tabs = [new SessionTab { Path = @"C:\a.txt" }] };

        session.Save(state);
        session.Save(state with { Tabs = [new SessionTab { Path = @"C:\A.TXT" }] });

        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Too_many_logs_are_cut_to_the_limit()
    {
        var store = new MemoryStateStore();
        var tabs = Enumerable.Range(0, SessionService.MaxTabs + 5).Select(i => new SessionTab { Path = $@"C:\{i}.txt" }).ToList();

        new SessionService(store).Save(new SessionState { Tabs = tabs });

        Assert.Equal(SessionService.MaxTabs, store.State.Session.Tabs.Count);
    }
}
