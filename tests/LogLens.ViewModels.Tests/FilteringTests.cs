using LogLens.Core.Models;

namespace LogLens.ViewModels.Tests;

internal static class Docs
{
    public const string Sample =
        "2026-11-13 08:00:00.000 +05:30 [INF] Order 1 shipped\n" +
        "2026-11-13 08:01:00.000 +05:30 [WRN] Retry for payment\n" +
        "2026-11-13 08:02:00.000 +05:30 [ERR] Payment failed for order 2\n" +
        "System.InvalidOperationException: boom\n" +
        "   at Shop.Pay.Charge()\n" +
        "2026-11-13 08:03:00.000 +05:30 [DBG] cache hit\n" +
        "2026-11-13 08:10:00.000 +05:30 [INF] Order 3 shipped\n" +
        "2026-11-13 08:20:00.000 +05:30 [FTL] Shutting down\n";

    public static LogViewModel Vm(string text = Sample) => new(TestSupport.Document(text, "sample.txt"));
}

public class LogViewModelFilterTests
{
    private static string[] Messages(LogViewModel vm) => vm.View.Select(e => e.Message).ToArray();

    [Fact]
    public void Starts_unfiltered_with_a_toggle_per_level_and_counts()
    {
        var vm = Docs.Vm();

        Assert.Equal(6, vm.LevelToggles.Count);
        Assert.Equal(2, vm.LevelToggles.Single(t => t.Level == LogLevel.Information).Count);
        Assert.Equal(1, vm.LevelToggles.Single(t => t.Level == LogLevel.Error).Count);
        Assert.Equal(0, vm.MinLevelIndex);
        Assert.False(vm.HasActiveFilters);
        Assert.False(vm.View.IsFiltered);
        Assert.Equal(6, vm.View.Count);
    }

    [Fact]
    public async Task Turning_a_level_off_hides_those_events_and_marks_the_preset_custom()
    {
        var vm = Docs.Vm();

        vm.LevelToggles.Single(t => t.Level == LogLevel.Error).IsOn = false;
        await vm.WhenSettledAsync();

        Assert.DoesNotContain("Payment failed for order 2", Messages(vm));
        Assert.Equal(5, vm.View.Count);
        Assert.True(vm.HasActiveFilters);
        Assert.Equal(-1, vm.MinLevelIndex);
        Assert.Contains("5 of 6 events", vm.StatusText);
    }

    [Fact]
    public async Task Minimum_level_preset_sets_the_toggles()
    {
        var vm = Docs.Vm();

        vm.MinLevelIndex = 3; // Warning and above
        await vm.WhenSettledAsync();

        Assert.Equal(3, vm.View.Count);
        Assert.All(vm.View, e => Assert.True(e.Level >= LogLevel.Warning));
        Assert.Equal(
            new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Fatal },
            vm.LevelToggles.Where(t => t.IsOn).Select(t => t.Level));
    }

    [Fact]
    public async Task Toggling_back_to_a_preset_selects_it_in_the_combo()
    {
        var vm = Docs.Vm();
        vm.MinLevelIndex = 3;
        await vm.WhenSettledAsync();

        vm.LevelToggles.Single(t => t.Level == LogLevel.Information).IsOn = true;
        vm.LevelToggles.Single(t => t.Level == LogLevel.Debug).IsOn = true;
        vm.LevelToggles.Single(t => t.Level == LogLevel.Verbose).IsOn = true;
        await vm.WhenSettledAsync();

        Assert.Equal(0, vm.MinLevelIndex); // everything on == "All levels"
        Assert.False(vm.HasActiveFilters);
        Assert.False(vm.View.IsFiltered);
    }

    [Fact]
    public void Unknown_level_toggle_only_exists_when_a_parsed_event_has_one()
    {
        Assert.DoesNotContain(Docs.Vm().LevelToggles, t => t.Level == LogLevel.Unknown);

        var withOdd = Docs.Vm(Docs.Sample + "2026-11-13 08:30:00.000 +05:30 [XYZ] odd level\n");
        Assert.Contains(withOdd.LevelToggles, t => t.Level == LogLevel.Unknown);
    }

    [Fact]
    public async Task A_valid_query_filters_and_an_invalid_one_keeps_the_last_result_and_reports_the_error()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l >= 'Error'";
        await vm.WhenSettledAsync();
        Assert.Equal(2, vm.View.Count);
        Assert.Null(vm.QueryError);

        vm.QueryText = "@l >= 'Error' and @m =";
        await vm.WhenSettledAsync();

        Assert.True(vm.HasQueryError);
        Assert.Contains("position", vm.QueryError);
        Assert.Equal(2, vm.View.Count); // still the last good filter

        vm.QueryText = "@m contains 'order'";
        await vm.WhenSettledAsync();

        Assert.False(vm.HasQueryError);
        Assert.Equal(3, vm.View.Count);
    }

    [Fact]
    public async Task Other_filters_keep_working_with_the_last_valid_query_while_the_query_is_broken()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l >= 'Warning'";
        await vm.WhenSettledAsync();
        Assert.Equal(3, vm.View.Count); // Warning, Error, Fatal

        vm.QueryText = "@l >= 'Loud'"; // not a level: broken
        vm.LevelToggles.Single(t => t.Level == LogLevel.Fatal).IsOn = false;
        await vm.WhenSettledAsync();

        Assert.True(vm.HasQueryError);
        Assert.Equal(["Retry for payment", "Payment failed for order 2"], Messages(vm)); // last good query + the level toggle
        Assert.True(vm.HasActiveFilters);
    }

    [Fact]
    public async Task Clearing_the_query_text_removes_the_filter()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l = 'Fatal'";
        await vm.WhenSettledAsync();
        Assert.Single(vm.View);

        vm.QueryText = string.Empty;
        await vm.WhenSettledAsync();

        Assert.False(vm.View.IsFiltered);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task Unparsed_lines_get_a_toggle_of_their_own()
    {
        var vm = Docs.Vm("# stray header\n" + Docs.Sample);
        Assert.Equal(7, vm.View.Count);
        var unparsed = vm.LevelToggles.Single(t => t.Level == LogLevel.Unparsed);
        Assert.Equal(1, unparsed.Count);
        Assert.Equal("Unparsed", unparsed.Label);

        unparsed.IsOn = false;
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.View.Count);
        Assert.DoesNotContain(vm.View, e => e.IsUnparsed);
        Assert.Equal(-1, vm.MinLevelIndex);
    }

    [Fact]
    public async Task A_minimum_level_leaves_out_lines_without_a_severity()
    {
        var vm = Docs.Vm("# stray header\n" + Docs.Sample);

        vm.MinLevelIndex = 1; // Debug and above
        await vm.WhenSettledAsync();

        Assert.False(vm.LevelToggles.Single(t => t.Level == LogLevel.Unparsed).IsOn);
        Assert.Equal(6, vm.View.Count);
    }

    [Fact]
    public async Task Levels_query_and_time_combine()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@m contains 'order'";
        vm.TimeFromText = "2026-11-13 08:05";
        Assert.True(vm.ApplyTimeBoxes());
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 3 shipped"], Messages(vm));
    }

    [Fact]
    public async Task Filtering_reports_how_long_it_took()
    {
        var vm = Docs.Vm();
        Assert.Null(vm.LastFilterMilliseconds);

        vm.QueryText = "@l = 'Fatal'";
        await vm.WhenSettledAsync();

        Assert.NotNull(vm.LastFilterMilliseconds);
        Assert.Contains("filtered in", vm.StatusText);
    }

    [Fact]
    public async Task FilterApplied_carries_the_event_that_was_active()
    {
        var vm = Docs.Vm();
        var active = vm.View[2];
        vm.SelectedEvent = active;
        LogEvent? seen = null;
        vm.FilterApplied += (_, e) => seen = e;

        vm.QueryText = "@l >= 'Warning'";
        await vm.WhenSettledAsync();

        Assert.Same(active, seen);
        Assert.True(vm.View.IndexOf(active) >= 0);
    }

    [Fact]
    public async Task ClearFilters_resets_everything_at_once()
    {
        var vm = Docs.Vm("# stray\n" + Docs.Sample);
        vm.LevelToggles.Single(t => t.Level == LogLevel.Debug).IsOn = false;
        vm.QueryText = "@l >= 'Information'";
        vm.TimeFromText = "2026-11-13 08:00:30";
        vm.ApplyTimeBoxes();
        vm.LevelToggles.Single(t => t.Level == LogLevel.Unparsed).IsOn = false;
        vm.ShowOnlyLike(vm.View[1]);
        vm.HideLike(vm.View[2]);
        vm.FindBarVisible = true;
        vm.SearchText = "order";
        vm.OnlyMatches = true;
        await vm.WhenSettledAsync();
        Assert.True(vm.HasActiveFilters);

        vm.ClearFilters();
        await vm.WhenSettledAsync();

        Assert.False(vm.HasActiveFilters);
        Assert.False(vm.View.IsFiltered);
        Assert.Equal(7, vm.View.Count);
        Assert.All(vm.LevelToggles, t => Assert.True(t.IsOn));
        Assert.Equal(string.Empty, vm.QueryText);
        Assert.Equal(string.Empty, vm.TimeFromText);
        Assert.False(vm.OnlyMatches);
        Assert.False(vm.HasActivePin);
        Assert.False(vm.HasHiddenGroups);
        Assert.Equal(0, vm.MinLevelIndex);
        Assert.Equal("Time", vm.TimeButtonLabel);
    }
}

public class LogViewModelTimeTests
{
    [Fact]
    public async Task Last_N_minutes_ends_at_the_last_event_not_at_the_clock()
    {
        var vm = Docs.Vm();

        vm.SetLast(TimeSpan.FromMinutes(11)); // last event 08:20 -> from 08:09
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 3 shipped", "Shutting down"], vm.View.Select(e => e.Message));
        Assert.True(vm.IsTimeFiltered);
        Assert.Contains("08:09:00", vm.TimeFromText);
        Assert.Equal(string.Empty, vm.TimeToText);
    }

    [Fact]
    public async Task Around_the_selected_event_uses_its_time()
    {
        var vm = Docs.Vm();
        vm.SelectedEvent = vm.View[1]; // 08:01
        vm.SetAround(TimeSpan.FromMinutes(1.5));
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 1 shipped", "Retry for payment", "Payment failed for order 2"], vm.View.Select(e => e.Message));
    }

    [Fact]
    public void Around_without_a_selection_asks_for_one()
    {
        var vm = Docs.Vm();

        vm.SetAround(TimeSpan.FromMinutes(1));

        Assert.Contains("Select an event", vm.TimeError);
        Assert.False(vm.IsTimeFiltered);
    }

    [Theory]
    [InlineData("yesterday", "", "\"From\"")]
    [InlineData("", "later", "\"To\"")]
    [InlineData("2026-11-13 09:00", "2026-11-13 08:00", "later than")]
    public void Bad_time_boxes_report_an_error_and_change_nothing(string from, string to, string fragment)
    {
        var vm = Docs.Vm();
        vm.TimeFromText = from;
        vm.TimeToText = to;

        Assert.False(vm.ApplyTimeBoxes());

        Assert.Contains(fragment, vm.TimeError);
        Assert.True(vm.HasTimeError);
        Assert.False(vm.IsTimeFiltered);
    }

    [Fact]
    public async Task Time_only_boxes_take_the_date_of_the_first_event()
    {
        var vm = Docs.Vm();
        vm.TimeFromText = "08:02";
        vm.TimeToText = "08:03:30";

        Assert.True(vm.ApplyTimeBoxes());
        await vm.WhenSettledAsync();

        Assert.Equal(["Payment failed for order 2", "cache hit"], vm.View.Select(e => e.Message));
        Assert.Equal("Time: 11-13 08:02:00 – 11-13 08:03:30", vm.TimeButtonLabel);
    }

    [Fact]
    public async Task ClearTime_removes_the_window()
    {
        var vm = Docs.Vm();
        vm.SetLast(TimeSpan.FromMinutes(5));
        await vm.WhenSettledAsync();
        Assert.True(vm.View.IsFiltered);

        vm.ClearTime();
        await vm.WhenSettledAsync();

        Assert.False(vm.View.IsFiltered);
        Assert.False(vm.IsTimeFiltered);
        Assert.Equal("Time", vm.TimeButtonLabel);
    }
}

public class LogViewModelSearchTests
{
    private static async Task<LogViewModel> Searching(string text, Action<LogViewModel>? configure = null, string? doc = null)
    {
        var vm = Docs.Vm(doc ?? Docs.Sample);
        configure?.Invoke(vm);
        vm.FindBarVisible = true;
        vm.SearchText = text;
        await vm.WhenSettledAsync();
        return vm;
    }

    [Fact]
    public async Task Search_counts_matches_and_offers_a_highlighter()
    {
        var vm = await Searching("order");

        Assert.Equal(3, vm.MatchCount); // Order 1, Payment failed for order 2, Order 3
        Assert.Equal("3 matches", vm.MatchSummary);
        Assert.NotNull(vm.CurrentMatcher);
        Assert.NotNull(vm.Highlighter);
        Assert.Equal([(0, 5)], vm.Highlighter!("Order 1 shipped"));
    }

    [Fact]
    public async Task Next_and_previous_walk_the_matches_and_wrap()
    {
        var vm = await Searching("order");

        var first = vm.StepMatch(-1, forward: true);
        var second = vm.StepMatch(first, forward: true);
        var third = vm.StepMatch(second, forward: true);
        var wrapped = vm.StepMatch(third, forward: true);
        var back = vm.StepMatch(first, forward: false);

        Assert.Equal([0, 2, 4], new[] { first, second, third });
        Assert.Equal(first, wrapped);
        Assert.Equal(third, back);
    }

    [Fact]
    public async Task Summary_shows_the_position_of_the_active_row()
    {
        var vm = await Searching("order");

        vm.UpdateActiveRow(2);

        Assert.Equal("2 of 3", vm.MatchSummary);

        vm.UpdateActiveRow(1); // not a match
        Assert.Equal("3 matches", vm.MatchSummary);
    }

    [Fact]
    public async Task No_matches_says_so_and_navigation_returns_minus_one()
    {
        var vm = await Searching("zzz-not-there");

        Assert.Equal("No matches", vm.MatchSummary);
        Assert.Equal(-1, vm.StepMatch(0, true));
    }

    [Fact]
    public async Task Search_options_change_the_result()
    {
        var plain = await Searching("ORDER");
        Assert.Equal(3, plain.MatchCount);

        var caseSensitive = await Searching("ORDER", v => v.MatchCase = true);
        Assert.Equal(0, caseSensitive.MatchCount);

        var word = await Searching("order", v => v.WholeWord = true);
        Assert.Equal(3, word.MatchCount);

        var regex = await Searching(@"order \d", v => v.UseRegex = true);
        Assert.Equal(3, regex.MatchCount);
    }

    [Fact]
    public async Task Exception_text_is_searched_only_when_the_option_is_on()
    {
        var withException = await Searching("InvalidOperation");
        Assert.Equal(1, withException.MatchCount);

        var without = await Searching("InvalidOperation", v => v.IncludeException = false);
        Assert.Equal(0, without.MatchCount);
    }

    [Fact]
    public async Task An_invalid_regex_reports_an_error_and_has_no_matcher()
    {
        var vm = await Searching("(unclosed", v => v.UseRegex = true);

        Assert.True(vm.HasSearchError);
        Assert.Null(vm.CurrentMatcher);
        Assert.Equal(string.Empty, vm.MatchSummary);

        vm.SearchText = "order";
        await vm.WhenSettledAsync();
        Assert.False(vm.HasSearchError);
    }

    [Fact]
    public async Task Closing_the_find_bar_ends_the_search()
    {
        var vm = await Searching("order");
        Assert.NotNull(vm.CurrentMatcher);

        vm.FindBarVisible = false;
        await vm.WhenSettledAsync();

        Assert.Null(vm.CurrentMatcher);
        Assert.Null(vm.Highlighter);
        Assert.Equal(string.Empty, vm.MatchSummary);

        vm.FindBarVisible = true;
        await vm.WhenSettledAsync();
        Assert.NotNull(vm.CurrentMatcher); // text is remembered
    }

    [Fact]
    public async Task Only_matches_narrows_the_list_and_turning_it_off_restores_it()
    {
        var vm = await Searching("order");

        vm.OnlyMatches = true;
        await vm.WhenSettledAsync();
        Assert.Equal(3, vm.View.Count);
        Assert.True(vm.HasActiveFilters);
        Assert.Equal(3, vm.MatchCount); // every visible row matches

        vm.OnlyMatches = false;
        await vm.WhenSettledAsync();
        Assert.Equal(6, vm.View.Count);
        Assert.False(vm.HasActiveFilters);
        Assert.Equal(3, vm.MatchCount);
    }

    [Fact]
    public async Task Closing_the_bar_also_leaves_only_matches_mode()
    {
        var vm = await Searching("order");
        vm.OnlyMatches = true;
        await vm.WhenSettledAsync();

        vm.FindBarVisible = false;
        await vm.WhenSettledAsync();

        Assert.False(vm.OnlyMatches);
        Assert.Equal(6, vm.View.Count);
    }

    [Fact]
    public async Task Matches_are_counted_within_the_filtered_list_and_rows_are_view_relative()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l >= 'Warning'"; // Retry, Payment failed, Shutting down
        await vm.WhenSettledAsync();

        vm.FindBarVisible = true;
        vm.SearchText = "payment";
        await vm.WhenSettledAsync();

        Assert.Equal(2, vm.MatchCount); // "Retry for payment" and "Payment failed for order 2"
        Assert.Equal(0, vm.StepMatch(-1, true));
        Assert.Equal(1, vm.StepMatch(0, true));
    }

    [Fact]
    public async Task Filtering_after_searching_recounts_matches()
    {
        var vm = await Searching("order");
        Assert.Equal(3, vm.MatchCount);

        vm.QueryText = "@l = 'Information'";
        await vm.WhenSettledAsync();

        Assert.Equal(2, vm.MatchCount); // Order 1, Order 3
    }

    [Fact]
    public async Task SearchChanged_fires_so_the_grid_can_repaint_highlights()
    {
        var vm = Docs.Vm();
        var raised = 0;
        vm.SearchChanged += (_, _) => raised++;

        vm.FindBarVisible = true;
        vm.SearchText = "order";
        await vm.WhenSettledAsync();

        Assert.True(raised >= 1);
    }
}

public class LogViewModelGoToTests
{
    [Fact]
    public void Event_number_resolves_to_its_row()
    {
        var vm = Docs.Vm();

        var (row, error) = vm.ResolveGoTo("#3");

        Assert.Null(error);
        Assert.Equal(2, row);
    }

    [Fact]
    public void Event_number_beyond_the_file_is_an_error()
    {
        var (row, error) = Docs.Vm().ResolveGoTo("99");

        Assert.Equal(-1, row);
        Assert.Contains("only has 6", error);
    }

    [Fact]
    public async Task Event_number_hidden_by_a_filter_says_so()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l = 'Fatal'";
        await vm.WhenSettledAsync();

        var (row, error) = vm.ResolveGoTo("1");

        Assert.Equal(-1, row);
        Assert.Contains("hidden by the current filters", error);
    }

    [Fact]
    public async Task Event_number_resolves_to_the_row_in_the_filtered_list()
    {
        var vm = Docs.Vm();
        vm.QueryText = "@l >= 'Warning'";
        await vm.WhenSettledAsync();

        var (row, error) = vm.ResolveGoTo("3"); // event #3 is the Error, second row of the filtered list

        Assert.Null(error);
        Assert.Equal(1, row);
    }

    [Fact]
    public void Time_resolves_to_the_first_event_at_or_after_it()
    {
        var vm = Docs.Vm();

        var (row, error) = vm.ResolveGoTo("08:05"); // between 08:03 and 08:10

        Assert.Null(error);
        Assert.Equal("Order 3 shipped", vm.View[row].Message);
    }

    [Fact]
    public void Garbage_is_reported()
    {
        var (row, error) = Docs.Vm().ResolveGoTo("soon");

        Assert.Equal(-1, row);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}


public class LogViewModelQuickFilterTests
{
    private static string[] Messages(LogViewModel vm) => vm.View.Select(e => e.Message).ToArray();

    [Fact]
    public async Task Show_only_like_keeps_events_with_the_same_message_shape()
    {
        var vm = Docs.Vm();

        vm.ShowOnlyLike(vm.View[0]); // "Order 1 shipped"
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 1 shipped", "Order 3 shipped"], Messages(vm));
        Assert.True(vm.HasActivePin);
        Assert.Equal("Only: Order {n} shipped", vm.ActivePinLabel);

        vm.ClearPin();
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.View.Count);
        Assert.False(vm.HasActivePin);
    }

    [Fact]
    public async Task Hide_like_adds_up_and_can_be_undone_at_once()
    {
        var vm = Docs.Vm();

        vm.HideLike(vm.View[0]); // Order {n} shipped
        vm.HideLike(vm.View[0]); // the same group again: no duplicate
        await vm.WhenSettledAsync();
        Assert.Equal("Hidden: Order {n} shipped", vm.HiddenGroupsLabel);

        vm.HideLike(vm.View[0]); // now "Retry for payment"
        await vm.WhenSettledAsync();

        Assert.Equal(["Payment failed for order 2", "cache hit", "Shutting down"], Messages(vm));
        Assert.Equal("Hidden: 2 kinds of event", vm.HiddenGroupsLabel);
        Assert.Equal("Order {n} shipped\nRetry for payment", vm.HiddenGroupsList);

        vm.ClearHiddenGroups();
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.View.Count);
        Assert.False(vm.HasHiddenGroups);
    }

    [Fact]
    public async Task Unparsed_lines_have_no_shape_to_pin_or_hide()
    {
        var vm = Docs.Vm("# stray header\n" + Docs.Sample);

        vm.ShowOnlyLike(vm.View[0]);
        vm.HideLike(vm.View[0]);
        await vm.WhenSettledAsync();

        Assert.False(vm.HasActivePin);
        Assert.False(vm.HasHiddenGroups);
        Assert.False(vm.View.IsFiltered);
    }

    [Fact]
    public async Task Show_only_and_hide_a_level()
    {
        var vm = Docs.Vm();

        vm.ShowOnlyLevel(LogLevel.Information);
        await vm.WhenSettledAsync();
        Assert.Equal(["Order 1 shipped", "Order 3 shipped"], Messages(vm));

        vm.ClearFilters();
        vm.HideLevel(LogLevel.Information);
        await vm.WhenSettledAsync();
        Assert.Equal(4, vm.View.Count);
        Assert.Equal(-1, vm.MinLevelIndex);

        Assert.False(vm.CanFilterByLevel(LogLevel.Unparsed));
    }

    [Fact]
    public async Task Around_the_selected_event_is_a_quick_filter_too()
    {
        var vm = Docs.Vm();
        vm.SelectedEvent = vm.View[4]; // 08:10

        Assert.True(vm.SetAround(TimeSpan.FromSeconds(30)));
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 3 shipped"], Messages(vm));
    }

    [Fact]
    public async Task Only_matches_can_show_context_events_around_each_match()
    {
        var vm = Docs.Vm();
        vm.FindBarVisible = true;
        vm.SearchText = "cache";
        vm.OnlyMatches = true;
        vm.ContextEvents = 1;
        await vm.WhenSettledAsync();

        Assert.Equal(["Payment failed for order 2", "cache hit", "Order 3 shipped"], Messages(vm));
        Assert.Equal(1, vm.MatchCount);

        vm.ContextEvents = 500;
        Assert.Equal(LogViewModel.MaxContextEvents, vm.ContextEvents);
    }

    [Fact]
    public void Next_and_previous_problem_skip_the_rest_and_wrap()
    {
        var vm = Docs.Vm(); // INF, WRN, ERR, DBG, INF, FTL

        Assert.Equal(1, vm.NextProblemRow(-1, forward: true));
        Assert.Equal(2, vm.NextProblemRow(1, forward: true));
        Assert.Equal(5, vm.NextProblemRow(2, forward: true));
        Assert.Equal(1, vm.NextProblemRow(5, forward: true));
        Assert.Equal(5, vm.NextProblemRow(1, forward: false));
        Assert.Equal(5, vm.NextProblemRow(-1, forward: false));
    }

    [Fact]
    public async Task The_status_bar_counts_what_is_shown_out_of_all_events()
    {
        var vm = Docs.Vm();
        Assert.StartsWith("6 events", vm.StatusText);

        vm.MinLevelIndex = 4; // Error and above
        await vm.WhenSettledAsync();

        Assert.StartsWith("2 of 6 events", vm.StatusText);
    }

    [Fact]
    public async Task An_empty_result_is_announced_rather_than_shown_as_a_blank_list()
    {
        var vm = Docs.Vm();
        Assert.False(vm.ShowNoMatches);

        vm.QueryText = "@m contains 'nothing like this'";
        await vm.WhenSettledAsync();

        Assert.True(vm.ShowNoMatches);
        Assert.Empty(vm.View);
    }
}
