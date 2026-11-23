using LogLens.Core.Collections;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.Core.Tests;

/// <summary>Newest first (FR-G3), relative times (FR-G5), wrapped messages (FR-G6), the column layout (FR-G1) and the settings that remember them.</summary>
public class NewestFirstTests
{
    private static LogEvent[] Events(int count)
        => Enumerable.Range(0, count)
            .Select(i => new LogEvent(i, null, 0, 0, new DateTimeOffset(2026, 11, 13, 8, 0, i, TimeSpan.Zero), i % 3 == 0 ? LogLevel.Error : LogLevel.Information, "event " + i, null))
            .ToArray();

    [Fact]
    public void Rows_run_from_the_newest_event_down_and_the_chronological_view_stays_oldest_first()
    {
        var view = new LogEventView(new LogEventList().Fill(Events(5)));

        Assert.True(view.SetNewestFirst(true));

        Assert.Equal([4, 3, 2, 1, 0], view.Select(e => e.Id));
        Assert.Equal([0, 1, 2, 3, 4], view.Chronological.Select(e => e.Id));
        Assert.Equal(0, view.RowOfPosition(4));
        Assert.Equal(4, view.RowOfPosition(0));
        Assert.Equal(-1, view.RowOfPosition(-1));
    }

    [Fact]
    public void Setting_the_same_order_changes_nothing()
    {
        var view = new LogEventView(Events(3));

        Assert.False(view.SetNewestFirst(false));
        Assert.True(view.SetNewestFirst(true));
        Assert.False(view.SetNewestFirst(true));
    }

    [Fact]
    public void IndexOf_gives_the_row_in_either_order_filtered_or_not()
    {
        var events = Events(6);
        var view = new LogEventView(events);
        view.SetNewestFirst(true);

        Assert.Equal(5, view.IndexOf(events[0]));
        Assert.Equal(0, view.IndexOf(events[5]));

        view.SetFilter([0, 3]); // the two errors
        Assert.Equal(1, view.IndexOf(events[0]));
        Assert.Equal(0, view.IndexOf(events[3]));
        Assert.Equal(-1, view.IndexOf(events[1]));
    }

    [Fact]
    public void FindRows_returns_ascending_rows_of_the_current_order()
    {
        var view = new LogEventView(Events(7));
        view.SetNewestFirst(true);

        var rows = view.FindRows(e => e.Id % 3 == 0); // events 0, 3, 6

        Assert.Equal([0, 3, 6], rows); // rows 0, 3, 6 hold events 6, 3, 0
        Assert.All(rows, row => Assert.Equal(0, view[row].Id % 3));
    }

    [Fact]
    public void New_events_appear_at_the_top_and_the_change_is_reported_there()
    {
        var list = new LogEventList().Fill(Events(3));
        var view = new LogEventView(list);
        view.SetNewestFirst(true);

        foreach (var e in Events(5).Skip(3))
        {
            list.Add(e);
        }

        var (first, removed, added) = view.Sync(null, 3);

        Assert.Equal((0, 0, 2), (first, removed, added));
        Assert.Equal([4, 3, 2, 1, 0], view.Select(e => e.Id));
    }

    [Fact]
    public void A_filtered_view_appends_matching_events_at_the_top()
    {
        var list = new LogEventList().Fill(Events(4)); // errors: 0, 3
        var view = new LogEventView(list);
        Func<LogEvent, bool> errors = e => e.Level == LogLevel.Error;
        view.SetFilter(LogEventView.ComputeIndices(list, errors));
        view.SetNewestFirst(true);

        foreach (var e in Events(8).Skip(4))
        {
            list.Add(e);
        }

        var (first, removed, added) = view.Sync(errors, 4); // new errors: 6

        Assert.Equal((0, 0, 1), (first, removed, added));
        Assert.Equal([6, 3, 0], view.Select(e => e.Id));
    }

    // ------------------------------------------------------------------ selection

    [Fact]
    public void Inserting_rows_at_the_start_moves_the_selection_with_its_events()
    {
        var m = new SelectionModel();
        m.Reset(5);
        m.SelectSingle(1);
        m.Toggle(3);

        m.InsertAtStart(2);

        Assert.Equal(7, m.ItemCount);
        Assert.Equal([3, 5], m.SelectedIndices());
        Assert.Equal(2, m.Count);
        Assert.Equal(5, m.Active);
        Assert.Equal(5, m.Anchor);
        Assert.False(m.IsSelected(0));
    }

    [Fact]
    public void Inserting_nothing_changes_nothing_and_an_empty_selection_stays_empty()
    {
        var m = new SelectionModel();
        m.Reset(3);

        m.InsertAtStart(0);
        m.InsertAtStart(4);

        Assert.Equal(7, m.ItemCount);
        Assert.Equal(-1, m.Active);
        Assert.Equal(-1, m.Anchor);
        Assert.Empty(m.SelectedIndices());
    }
}

public class TimeColumnTests
{
    private static LogEvent At(int id, double seconds)
        => new(id, null, 0, 0, new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero).AddSeconds(seconds), LogLevel.Information, "m", null);

    private static LogEvent Unparsed() => new(9, null, 0, 0, DateTimeOffset.MinValue, LogLevel.Unparsed, "x", null);

    [Theory]
    [InlineData(0.0, "+0 ms")]
    [InlineData(0.12, "+120 ms")]
    [InlineData(3.204, "+3.204 s")]
    [InlineData(59.999, "+59.999 s")]
    [InlineData(125, "+2 m 05 s")]
    [InlineData(3600 + 4 * 60, "+1 h 04 m")]
    [InlineData(2 * 86400 + 5 * 3600, "+2 d 05 h")]
    [InlineData(-2.5, "-2.500 s")]
    [InlineData(-125, "-2 m 05 s")]
    public void Elapsed_times_are_short_and_signed(double seconds, string expected)
        => Assert.Equal(expected, LogText.Delta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_timestamp_mode_always_shows_the_timestamp()
    {
        var a = At(0, 0);
        var b = At(1, 5);

        Assert.Equal(LogText.Time(b), LogText.TimeCell(b, a, TimeColumnMode.Timestamp));
        Assert.Equal("2026-11-13 08:00:05.000", LogText.Time(b));
        Assert.Equal("2026-11-13 08:00:05.000 +00:00", LogText.FullTime(b.Timestamp));
    }

    [Fact]
    public void A_relative_mode_shows_the_time_since_the_reference_and_keeps_the_reference_anchored()
    {
        var a = At(0, 0);
        var b = At(1, 5.25);

        Assert.Equal("+5.250 s", LogText.TimeCell(b, a, TimeColumnMode.SincePrevious));
        Assert.Equal("-5.250 s", LogText.TimeCell(a, b, TimeColumnMode.SinceSelected));
        Assert.Equal(LogText.Time(a), LogText.TimeCell(a, a, TimeColumnMode.SinceSelected));
    }

    [Fact]
    public void Rows_without_a_usable_reference_keep_their_timestamp()
    {
        var a = At(0, 0);

        Assert.Equal(LogText.Time(a), LogText.TimeCell(a, null, TimeColumnMode.SincePrevious));
        Assert.Equal(LogText.Time(a), LogText.TimeCell(a, Unparsed(), TimeColumnMode.SinceSelected));
        Assert.Equal("—", LogText.TimeCell(Unparsed(), a, TimeColumnMode.SincePrevious));
    }
}

public class ColumnLayoutTests
{
    [Fact]
    public void The_default_layout_gives_the_default_widths_and_a_single_line_row()
    {
        var m = GridMetrics.For(null, wrapMessages: false, hasSource: false);

        Assert.Equal(GridMetrics.Default, m);
        Assert.Equal(GridMetrics.BaseRowHeight, m.RowHeight);
        Assert.Equal(GridMetrics.DefaultTimeColumnWidth, m.TimeColumnWidth);
        Assert.Equal(GridMetrics.DefaultLevelColumnWidth, m.LevelColumnWidth);
        Assert.Equal(0, m.SourceColumnWidth); // only a merged view has a Source column
        Assert.Equal(1, m.MessageLines);
    }

    [Fact]
    public void A_merged_view_shows_the_source_column_unless_hidden()
    {
        Assert.Equal(GridMetrics.DefaultSourceColumnWidth, GridMetrics.For(null, false, hasSource: true).SourceColumnWidth);
        Assert.Equal(0, GridMetrics.For(new ColumnLayout { ShowSource = false }, false, hasSource: true).SourceColumnWidth);
    }

    [Fact]
    public void A_hidden_column_is_zero_wide_and_the_others_keep_their_width()
    {
        var noTime = GridMetrics.For(new ColumnLayout { ShowTime = false }, false, false);
        var noLevel = GridMetrics.For(new ColumnLayout { ShowLevel = false }, false, false);

        Assert.Equal(0, noTime.TimeColumnWidth);
        Assert.Equal(GridMetrics.DefaultLevelColumnWidth, noTime.LevelColumnWidth);
        Assert.Equal(0, noLevel.LevelColumnWidth);
        Assert.Equal(GridMetrics.DefaultTimeColumnWidth, noLevel.TimeColumnWidth);
    }

    [Fact]
    public void A_dragged_width_replaces_the_automatic_one_but_not_a_hidden_column()
    {
        var m = GridMetrics.For(new ColumnLayout { TimeWidth = 96, SourceWidth = 240 }, false, hasSource: true);

        Assert.Equal(96, m.TimeColumnWidth);
        Assert.Equal(240, m.SourceColumnWidth);
        Assert.Equal(0, GridMetrics.For(new ColumnLayout { ShowTime = false, TimeWidth = 200 }, false, false).TimeColumnWidth);
    }

    [Theory]
    [InlineData(5, ColumnLayout.MinWidth)]
    [InlineData(-30, ColumnLayout.MinWidth)]
    [InlineData(5000, ColumnLayout.MaxWidth)]
    [InlineData(120.4, 120)]
    public void Widths_are_rounded_and_kept_in_range(double dragged, double expected)
    {
        var layout = new ColumnLayout { TimeWidth = dragged, SourceWidth = dragged }.Normalised();

        Assert.Equal(expected, layout.TimeWidth);
        Assert.Equal(expected, layout.SourceWidth);
        Assert.Equal(expected, GridMetrics.For(new ColumnLayout { TimeWidth = dragged }, false, false).TimeColumnWidth);
    }

    [Fact]
    public void An_unusable_width_becomes_automatic()
    {
        var layout = new ColumnLayout { TimeWidth = double.NaN, SourceWidth = double.PositiveInfinity }.Normalised();

        Assert.Null(layout.TimeWidth);
        Assert.Null(layout.SourceWidth);
    }

    [Fact]
    public void Wrapping_makes_every_row_the_same_taller_height_and_changes_no_width()
    {
        var one = GridMetrics.For(null, wrapMessages: false, hasSource: true);
        var wrapped = GridMetrics.For(null, wrapMessages: true, hasSource: true);

        Assert.Equal(GridMetrics.WrappedMessageLines, wrapped.MessageLines);
        Assert.True(wrapped.RowHeight > one.RowHeight);
        Assert.Equal(one.TimeColumnWidth, wrapped.TimeColumnWidth);
        Assert.Equal(one.SourceColumnWidth, wrapped.SourceColumnWidth);
        Assert.True(wrapped.RowHeight >= wrapped.MessageLines * GridMetrics.LineHeight, "the lines must fit in the row");
    }
}

public class SettingsServiceTests
{
    private sealed class CountingStore : ISettingsStore
    {
        public AppSettings Stored { get; private set; } = new();

        public int Saves { get; private set; }

        public Exception? Failure { get; set; }

        public AppSettings Load() => Stored;

        public void Save(AppSettings settings)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Stored = settings;
            Saves++;
        }
    }

    [Fact]
    public void The_defaults_show_oldest_first_single_line_rows_and_every_column()
    {
        var current = new SettingsService(new CountingStore()).Current;

        Assert.False(current.NewestFirst);
        Assert.False(current.WrapMessages);
        Assert.False(current.ShowTimeline);
        Assert.Equal(new ColumnLayout(), current.Columns);
    }

    [Fact]
    public void An_update_is_saved_and_announced()
    {
        var store = new CountingStore();
        var service = new SettingsService(store);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        service.Update(s => s with { NewestFirst = true });

        Assert.True(service.Current.NewestFirst);
        Assert.True(store.Stored.NewestFirst);
        Assert.Equal(1, store.Saves);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void An_update_that_changes_nothing_neither_saves_nor_announces()
    {
        var store = new CountingStore();
        var service = new SettingsService(store);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        service.Update(s => s with { WrapMessages = false });
        service.Update(s => s with { Columns = new ColumnLayout() });

        Assert.Equal(0, store.Saves);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Dragged_widths_are_kept_in_range_before_they_are_saved()
    {
        var store = new CountingStore();
        var service = new SettingsService(store);

        service.Update(s => s with { Columns = s.Columns with { TimeWidth = 10_000 } });

        Assert.Equal(ColumnLayout.MaxWidth, service.Current.Columns.TimeWidth);
        Assert.Equal(ColumnLayout.MaxWidth, store.Stored.Columns.TimeWidth);
    }

    [Fact]
    public void Loaded_settings_are_normalised()
    {
        var store = new CountingStore();
        store.Save(new AppSettings { Columns = new ColumnLayout { TimeWidth = 1 } });

        Assert.Equal(ColumnLayout.MinWidth, new SettingsService(store).Current.Columns.TimeWidth);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_written_does_not_lose_the_change()
    {
        var store = new CountingStore { Failure = new UnauthorizedAccessException() };
        var service = new SettingsService(store);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        service.Update(s => s with { ShowTimeline = true });

        Assert.True(service.Current.ShowTimeline);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void The_in_memory_store_keeps_what_it_is_given()
    {
        var store = new InMemorySettingsStore();

        store.Save(new AppSettings { WrapMessages = true });

        Assert.True(store.Load().WrapMessages);
    }
}

internal static class LogEventListExtensions
{
    public static LogEventList Fill(this LogEventList list, IEnumerable<LogEvent> events)
    {
        foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }
}
