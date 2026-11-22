using LogLens.Core.Collections;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

internal static class Ev
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    public static LogEvent Make(
        LogLevel level = LogLevel.Information,
        string message = "hello",
        string? exception = null,
        string time = "2026-11-13 08:00:00.000",
        int id = 0,
        bool unparsed = false)
    {
        var ts = unparsed ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture), Ist);
        return new LogEvent(id, "t.txt", 0, 0, ts, unparsed ? LogLevel.Unparsed : level, message, exception);
    }
}

public class LevelMaskTests
{
    [Fact]
    public void All_allows_every_level_including_unknown_and_unparsed()
    {
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            Assert.True(LevelMask.Allows(LevelMask.All, level));
        }
    }

    [Fact]
    public void AtLeast_warning_keeps_warning_error_fatal_only()
    {
        var mask = LevelMask.AtLeast(LogLevel.Warning);

        Assert.True(LevelMask.Allows(mask, LogLevel.Warning));
        Assert.True(LevelMask.Allows(mask, LogLevel.Error));
        Assert.True(LevelMask.Allows(mask, LogLevel.Fatal));
        Assert.False(LevelMask.Allows(mask, LogLevel.Information));
        Assert.False(LevelMask.Allows(mask, LogLevel.Verbose));
        Assert.False(LevelMask.Allows(mask, LogLevel.Unknown));
        Assert.False(LevelMask.Allows(mask, LogLevel.Unparsed));
    }

    [Fact]
    public void AtLeast_unknown_means_everything()
        => Assert.Equal(LevelMask.All, LevelMask.AtLeast(LogLevel.Unknown));

    [Fact]
    public void With_sets_and_clears_single_levels()
    {
        var mask = LevelMask.With(LevelMask.All, LogLevel.Debug, false);

        Assert.False(LevelMask.Allows(mask, LogLevel.Debug));
        Assert.True(LevelMask.Allows(mask, LogLevel.Error));
        Assert.True(LevelMask.Allows(LevelMask.With(mask, LogLevel.Debug, true), LogLevel.Debug));
    }
}

public class TimeFilterTests
{
    [Fact]
    public void Wall_time_is_the_time_as_logged()
    {
        var t = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.FromMinutes(330)); // 02:30 UTC

        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 0), TimeRange.WallClock(t));
    }

    [Fact]
    public void Range_is_inclusive_and_open_ended_when_a_bound_is_missing()
    {
        var range = new TimeRange(new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 9, 0, 0));

        Assert.True(range.Contains(new DateTime(2026, 1, 1, 8, 0, 0)));
        Assert.True(range.Contains(new DateTime(2026, 1, 1, 9, 0, 0)));
        Assert.False(range.Contains(new DateTime(2026, 1, 1, 9, 0, 1)));
        Assert.True(new TimeRange(new DateTime(2026, 1, 1), null).Contains(DateTime.MaxValue.AddDays(-1)));
        Assert.True(new TimeRange(null, null).IsOpen);
    }

    [Fact]
    public void Last_and_Around_build_the_expected_windows()
    {
        var end = new DateTime(2026, 1, 1, 10, 0, 0);

        Assert.Equal(new TimeRange(end - TimeSpan.FromMinutes(5), null), TimeRange.Last(end, TimeSpan.FromMinutes(5)));
        Assert.Equal(new TimeRange(end - TimeSpan.FromSeconds(30), end + TimeSpan.FromSeconds(30)), TimeRange.Around(end, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("2026-11-13 08:00:05.301", 2026, 11, 13, 8, 0, 5, 301)]
    [InlineData("2026-11-13 08:00:05", 2026, 11, 13, 8, 0, 5, 0)]
    [InlineData("2026-11-13 08:00", 2026, 11, 13, 8, 0, 0, 0)]
    [InlineData("2026-11-13", 2026, 11, 13, 0, 0, 0, 0)]
    [InlineData("2026-11-13T08:00:05", 2026, 11, 13, 8, 0, 5, 0)]
    [InlineData("  2026-11-13 08:00  ", 2026, 11, 13, 8, 0, 0, 0)]
    public void Full_date_forms_parse(string text, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        Assert.True(TimeLiteral.TryParse(text, null, out var value));

        Assert.True(value.HasDate);
        Assert.Null(value.Instant);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), value.Wall);
    }

    [Fact]
    public void Time_only_takes_its_date_from_the_reference()
    {
        Assert.True(TimeLiteral.TryParse("08:15:30.5", new DateTime(2026, 11, 13, 22, 0, 0), out var value));

        Assert.True(value.HasDate);
        Assert.Equal(new DateTime(2026, 11, 13, 8, 15, 30, 500), value.Wall);
    }

    [Fact]
    public void Time_only_without_a_reference_has_no_date()
    {
        Assert.True(TimeLiteral.TryParse("08:15", null, out var value));

        Assert.False(value.HasDate);
    }

    [Theory]
    [InlineData("2026-11-13T08:00:00Z", 8, 0)]
    [InlineData("2026-11-13T08:00:00+05:30", 2, 30)]
    [InlineData("2026-11-13 08:00:00-08:00", 16, 0)]
    public void Offsets_make_the_value_an_exact_instant(string text, int utcHour, int utcMinute)
    {
        Assert.True(TimeLiteral.TryParse(text, null, out var value));

        Assert.NotNull(value.Instant);
        Assert.Equal(new DateTime(2026, 11, 13, utcHour, utcMinute, 0, DateTimeKind.Utc), value.Instant!.Value.UtcDateTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yesterday")]
    [InlineData("2026-13-45")]
    [InlineData("25:00")]
    [InlineData("13/11/2026")]
    public void Garbage_is_rejected(string text)
        => Assert.False(TimeLiteral.TryParse(text, null, out _));

    [Fact]
    public void Null_is_rejected()
        => Assert.False(TimeLiteral.TryParse(null, null, out _));
}

public class SearchTests
{
    private static SearchMatcher Compile(string text, bool matchCase = false, bool word = false, bool regex = false, bool exception = true)
    {
        var matcher = new SearchSpec(text, matchCase, word, regex, exception).TryCompile(out var error);
        Assert.Null(error);
        return matcher!;
    }

    [Fact]
    public void Plain_search_is_a_case_insensitive_substring_by_default()
    {
        var m = Compile("payment");

        Assert.True(m.IsMatch(Ev.Make(message: "Payment failed for order 42")));
        Assert.False(m.IsMatch(Ev.Make(message: "Order shipped")));
    }

    [Fact]
    public void Match_case_is_respected()
    {
        var m = Compile("Payment", matchCase: true);

        Assert.True(m.IsMatch(Ev.Make(message: "Payment failed")));
        Assert.False(m.IsMatch(Ev.Make(message: "payment failed")));
    }

    [Fact]
    public void Whole_word_does_not_match_inside_other_words()
    {
        var m = Compile("order", word: true);

        Assert.True(m.IsMatch(Ev.Make(message: "Order 42 shipped")));
        Assert.True(m.IsMatch(Ev.Make(message: "bad (order)")));
        Assert.False(m.IsMatch(Ev.Make(message: "reorder queue")));
        Assert.False(m.IsMatch(Ev.Make(message: "orders shipped")));
    }

    [Fact]
    public void Whole_word_treats_regex_characters_literally_unless_regex_is_on()
    {
        var m = Compile("a.b", word: true);

        Assert.True(m.IsMatch(Ev.Make(message: "x a.b y")));
        Assert.False(m.IsMatch(Ev.Make(message: "x aXb y")));
    }

    [Fact]
    public void Regex_search_supports_patterns()
    {
        var m = Compile(@"order \d{4,}", regex: true);

        Assert.True(m.IsMatch(Ev.Make(message: "Payment failed for order 67204")));
        Assert.False(m.IsMatch(Ev.Make(message: "Payment failed for order 42")));
    }

    [Fact]
    public void Exception_text_is_searched_only_when_included()
    {
        var withException = Ev.Make(message: "boom", exception: "System.IO.IOException: disk full");

        Assert.True(Compile("disk full", exception: true).IsMatch(withException));
        Assert.False(Compile("disk full", exception: false).IsMatch(withException));
    }

    [Fact]
    public void Empty_text_and_bad_regex_give_no_matcher()
    {
        Assert.Null(new SearchSpec("").TryCompile(out var emptyError));
        Assert.Null(emptyError);

        Assert.Null(new SearchSpec("(unclosed", UseRegex: true).TryCompile(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FindRanges_returns_every_plain_occurrence()
    {
        var ranges = Compile("an").FindRanges("banana AN");

        Assert.Equal([(1, 2), (3, 2), (7, 2)], ranges);
    }

    [Fact]
    public void FindRanges_with_regex_skips_empty_matches()
    {
        var ranges = Compile(@"\d*", regex: true).FindRanges("ab12cd345");

        Assert.Equal([(2, 2), (6, 3)], ranges);
    }

    [Fact]
    public void FindRanges_is_capped()
    {
        var ranges = Compile("a").FindRanges(new string('a', 500));

        Assert.Equal(64, ranges.Count);
    }
}

public class FilterSpecTests
{
    private static readonly LogEvent[] Events =
    [
        Ev.Make(LogLevel.Information, "start", time: "2026-11-13 08:00:00", id: 0),
        Ev.Make(LogLevel.Warning, "slow query", time: "2026-11-13 08:05:00", id: 1),
        Ev.Make(LogLevel.Error, "Payment failed", "System.Exception: x", time: "2026-11-13 08:10:00", id: 2),
        Ev.Make(LogLevel.Debug, "cache hit", time: "2026-11-13 08:15:00", id: 3),
        Ev.Make(message: "stray header line", id: 4, unparsed: true),
    ];

    private static int[] Run(FilterSpec spec)
    {
        var predicate = spec.BuildPredicate();
        return predicate is null ? Enumerable.Range(0, Events.Length).ToArray() : LogEventView.ComputeIndices(Events, predicate);
    }

    [Fact]
    public void Default_spec_is_inactive_and_has_no_predicate()
    {
        var spec = new FilterSpec();

        Assert.False(spec.IsActive);
        Assert.Null(spec.BuildPredicate());
    }

    [Fact]
    public void Level_mask_filters()
    {
        Assert.Equal([1, 2], Run(new FilterSpec { Levels = LevelMask.AtLeast(LogLevel.Warning) }));
    }

    [Fact]
    public void Unparsed_lines_are_a_level_of_their_own()
    {
        Assert.Equal([0, 1, 2, 3], Run(new FilterSpec { Levels = LevelMask.With(LevelMask.All, LogLevel.Unparsed, false) }));
    }

    [Fact]
    public void Time_range_uses_wall_time_and_excludes_unparsed_lines()
    {
        var spec = new FilterSpec { Time = new TimeRange(new DateTime(2026, 11, 13, 8, 5, 0), new DateTime(2026, 11, 13, 8, 10, 0)) };

        Assert.Equal([1, 2], Run(spec));
    }

    [Fact]
    public void Conditions_are_anded_together()
    {
        QueryParser.TryParse("@m contains 'a'", out var query, out _);
        var spec = new FilterSpec { Levels = LevelMask.AtLeast(LogLevel.Warning), Query = query };

        // Warning or above: "slow query" (no 'a') and "Payment failed" (has 'a') -> only the latter passes both conditions.
        Assert.Equal([2], Run(spec));
    }

    [Fact]
    public void Only_matching_keeps_events_the_search_matches()
    {
        var matcher = new SearchSpec("failed").TryCompile(out _);

        Assert.Equal([2], Run(new FilterSpec { OnlyMatching = matcher }));
    }

    [Fact]
    public void A_pin_keeps_one_group_and_excluded_groups_are_left_out()
    {
        var events = new[]
        {
            Ev.Make(message: "Order 42 shipped", id: 0),
            Ev.Make(message: "Order 7 shipped", id: 1),
            Ev.Make(message: "Cache warmed", id: 2),
            Ev.Make(message: "Order 42 shipped", id: 3, unparsed: true),
        };
        var like = EventPin.Like(events[0])!;

        Assert.Equal([0, 1], LogEventView.ComputeIndices(events, new FilterSpec { Pin = like }.BuildPredicate()!));
        Assert.Equal([2, 3], LogEventView.ComputeIndices(events, new FilterSpec { Exclude = [like] }.BuildPredicate()!));
        Assert.Equal("Order {n} shipped", like.Label);
        Assert.Null(EventPin.Like(events[3]));
    }

    [Fact]
    public void Context_expansion_counts_only_the_events_other_filters_leave()
    {
        var events = Enumerable.Range(0, 10).Select(i => Ev.Make(i % 2 == 0 ? LogLevel.Information : LogLevel.Debug, i == 6 ? "hit" : "x", id: i)).ToArray();
        bool Visible(LogEvent e) => e.Level == LogLevel.Information;

        Assert.Equal([4, 6, 8], ContextExpander.Compute(events, Visible, e => e.Message == "hit", 1));
        Assert.Equal([6], ContextExpander.Compute(events, Visible, e => e.Message == "hit", 0));
        Assert.Equal([5, 6, 7], ContextExpander.Compute(events, null, e => e.Message == "hit", 1));
    }
}

public class MatchNavigatorTests
{
    private static readonly int[] Matches = [10, 20, 30];

    [Theory]
    [InlineData(-1, true, 10)]
    [InlineData(5, true, 10)]
    [InlineData(10, true, 20)]
    [InlineData(15, true, 20)]
    [InlineData(30, true, 10)]   // wraps
    [InlineData(99, true, 10)]   // wraps
    [InlineData(10, false, 30)]  // wraps backwards
    [InlineData(20, false, 10)]
    [InlineData(25, false, 20)]
    [InlineData(-1, false, 30)]
    [InlineData(99, false, 30)]
    public void Step_moves_to_the_neighbouring_match_and_wraps(int current, bool forward, int expected)
        => Assert.Equal(expected, MatchNavigator.Step(Matches, current, forward));

    [Fact]
    public void Step_with_no_matches_returns_minus_one()
        => Assert.Equal(-1, MatchNavigator.Step([], 3, true));

    [Fact]
    public void A_single_match_steps_to_itself()
        => Assert.Equal(7, MatchNavigator.Step([7], 7, true));

    [Theory]
    [InlineData(10, 1)]
    [InlineData(30, 3)]
    [InlineData(15, 0)]
    public void PositionOf_is_one_based_and_zero_for_non_matches(int current, int expected)
        => Assert.Equal(expected, MatchNavigator.PositionOf(Matches, current));
}

public class GoToTests
{
    [Theory]
    [InlineData("1200", 1200)]
    [InlineData("#42", 42)]
    [InlineData("  7 ", 7)]
    public void Event_numbers_parse(string input, int expected)
    {
        Assert.True(GoTo.TryParse(input, null, out var target, out _));

        Assert.Equal(new GoToTarget.EventNumber(expected), target);
    }

    [Fact]
    public void Zero_is_not_an_event_number()
    {
        Assert.False(GoTo.TryParse("0", null, out _, out var error));
        Assert.Contains("start at 1", error);
    }

    [Fact]
    public void Time_with_a_date_parses()
    {
        Assert.True(GoTo.TryParse("2026-11-13 08:00:05", null, out var target, out _));

        var time = Assert.IsType<GoToTarget.Time>(target);
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 5), time.Value.Wall);
    }

    [Fact]
    public void Time_only_uses_the_reference_date_or_asks_for_one()
    {
        Assert.True(GoTo.TryParse("08:00:05", new DateTime(2026, 11, 13), out var target, out _));
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 5), Assert.IsType<GoToTarget.Time>(target).Value.Wall);

        Assert.False(GoTo.TryParse("08:00:05", null, out _, out var error));
        Assert.Contains("date", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    public void Garbage_gives_a_helpful_error(string input)
    {
        Assert.False(GoTo.TryParse(input, null, out var target, out var error));

        Assert.Null(target);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void IndexAtOrAfter_finds_the_first_event_not_before_the_target()
    {
        var view = new[]
        {
            Ev.Make(time: "2026-11-13 08:00:00", id: 0),
            Ev.Make(time: "2026-11-13 08:05:00", id: 1),
            Ev.Make(time: "2026-11-13 08:10:00", id: 2),
        };
        TimeLiteral.TryParse("2026-11-13 08:03", null, out var target);

        Assert.Equal(1, GoTo.IndexAtOrAfter(view, target));
    }

    [Fact]
    public void IndexAtOrAfter_returns_the_last_event_when_the_target_is_later_and_skips_unparsed()
    {
        var view = new[]
        {
            Ev.Make(message: "junk", unparsed: true),
            Ev.Make(time: "2026-11-13 08:00:00", id: 1),
            Ev.Make(time: "2026-11-13 08:05:00", id: 2),
        };
        TimeLiteral.TryParse("2026-11-14", null, out var target);

        Assert.Equal(2, GoTo.IndexAtOrAfter(view, target));
        Assert.Equal(-1, GoTo.IndexAtOrAfter([Ev.Make(unparsed: true)], target));
    }

    [Fact]
    public void IndexAtOrAfter_honours_exact_instants()
    {
        var view = new[]
        {
            Ev.Make(time: "2026-11-13 08:00:00", id: 0), // 02:30 UTC
            Ev.Make(time: "2026-11-13 09:00:00", id: 1), // 03:30 UTC
        };
        TimeLiteral.TryParse("2026-11-13T03:00:00Z", null, out var target);

        Assert.Equal(1, GoTo.IndexAtOrAfter(view, target));
    }
}

public class LogEventViewTests
{
    [Fact]
    public void Indices_are_in_view_space_not_source_space()
    {
        var events = new[]
        {
            Ev.Make(message: "a", id: 0),
            Ev.Make(message: "match", id: 1),
            Ev.Make(message: "b", id: 2),
            Ev.Make(message: "match", id: 3),
        };
        var view = new LogEventView(events);
        view.SetFilter([1, 2, 3]); // view row 0 = source 1, row 1 = source 2, row 2 = source 3

        var rows = view.FindRows(e => e.Message == "match");

        Assert.Equal([0, 2], rows);
    }

    [Fact]
    public void Honours_cancellation()
    {
        var view = new LogEventView(new LogEvent[200_000].Select((_, i) => Ev.Make(id: i)).ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => view.FindRows(_ => true, cts.Token));
    }

    [Fact]
    public void A_filter_restricts_rows_and_IndexOf_maps_events_to_rows()
    {
        var events = Enumerable.Range(0, 5).Select(i => Ev.Make(id: i)).ToArray();
        var view = new LogEventView(events);

        Assert.False(view.IsFiltered);
        Assert.Equal(5, view.Count);
        Assert.Equal(3, view.IndexOf(events[3]));

        view.SetFilter([1, 3]);

        Assert.True(view.IsFiltered);
        Assert.Equal(2, view.Count);
        Assert.Equal(5, view.TotalCount);
        Assert.Same(events[3], view[1]);
        Assert.Equal(1, view.IndexOf(events[3]));
        Assert.Equal(-1, view.IndexOf(events[2]));
        Assert.Equal(-1, view.IndexOf(Ev.Make(id: 3))); // same id, different document
        Assert.Equal([events[1], events[3]], view.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => view[2]);

        view.SetFilter(null);

        Assert.Equal(5, view.Count);
    }
}

public class RowFinderTests
{
    [Fact]
    public void Next_problem_wraps_around_and_skips_unparsed_lines()
    {
        var rows = new[]
        {
            Ev.Make(LogLevel.Information, "a", id: 0),
            Ev.Make(LogLevel.Warning, "b", id: 1),
            Ev.Make(LogLevel.Information, "c", id: 2),
            Ev.Make(message: "junk", id: 3, unparsed: true),
            Ev.Make(LogLevel.Fatal, "d", id: 4),
            Ev.Make(LogLevel.Information, "e", id: 5),
        };

        Assert.Equal(1, RowFinder.Step(rows, 0, true, RowFinder.IsProblem));
        Assert.Equal(4, RowFinder.Step(rows, 1, true, RowFinder.IsProblem));
        Assert.Equal(1, RowFinder.Step(rows, 4, true, RowFinder.IsProblem)); // wraps
        Assert.Equal(4, RowFinder.Step(rows, 1, false, RowFinder.IsProblem)); // wraps backwards
        Assert.Equal(1, RowFinder.Step(rows, 4, false, RowFinder.IsProblem));
        Assert.Equal(1, RowFinder.Step(rows, -1, true, RowFinder.IsProblem));
        Assert.Equal(4, RowFinder.Step(rows, -1, false, RowFinder.IsProblem));
    }

    [Fact]
    public void Next_problem_with_none_or_only_the_current_one_finds_nothing()
    {
        Assert.Equal(-1, RowFinder.Step([Ev.Make(id: 0), Ev.Make(id: 1)], 0, true, RowFinder.IsProblem));
        Assert.Equal(-1, RowFinder.Step([Ev.Make(LogLevel.Error, id: 0), Ev.Make(id: 1)], 0, true, RowFinder.IsProblem));
        Assert.Equal(-1, RowFinder.Step([], 0, true, RowFinder.IsProblem));
    }

    [Fact]
    public void Only_a_single_problem_is_found_from_nothing_selected()
        => Assert.Equal(0, RowFinder.Step([Ev.Make(LogLevel.Error, id: 0)], -1, true, RowFinder.IsProblem));
}
