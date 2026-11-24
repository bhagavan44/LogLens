using LogLens.Core.Filtering;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.ViewModels.Tests;

/// <summary>How timestamps are shown in a tab (FR-G5, FR-P3).</summary>
public class TimeDisplayViewModelTests
{
    private static readonly TimeDisplay Utc = new(TimestampZone.Utc);

    [Fact]
    public void The_detail_pane_copy_and_bookmarks_show_the_chosen_zone()
    {
        var vm = Docs.Vm();
        var e = vm.View[0]; // 08:00:00.000 +05:30 = 02:30 UTC
        vm.SelectedEvent = e;
        vm.ToggleBookmark(e);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.SetTimeDisplay(Utc);

        Assert.Contains(nameof(LogViewModel.DetailHeader), changed);
        Assert.Contains("2026-11-13 02:30:00.000 +00:00", vm.DetailHeader);
        Assert.StartsWith("2026-11-13 02:30:00.000\tINF", vm.BuildCopyText([e], raw: false));
        Assert.Equal("2026-11-13 02:30:00.000", vm.Bookmarks.Single().TimeText);
        Assert.Contains("+05:30", vm.BuildCopyText([e], raw: true)); // the original line is never changed
    }

    [Fact]
    public async Task The_query_bar_reads_times_in_the_chosen_zone()
    {
        var vm = Docs.Vm();
        vm.SetTimeDisplay(Utc);
        vm.QueryText = "@t >= '2026-11-13 02:40'"; // 08:10 as logged
        vm.ApplyQueryNow();
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 3 shipped", "Shutting down"], vm.View.Select(e => e.Message));
    }

    [Fact]
    public async Task A_typed_time_window_is_read_again_in_the_new_zone()
    {
        var vm = Docs.Vm();
        vm.TimeFromText = "2026-11-13 08:05";
        Assert.True(vm.ApplyTimeBoxes());
        await vm.WhenSettledAsync();
        Assert.Equal(2, vm.View.Count); // 08:10 and 08:20 as logged

        vm.SetTimeDisplay(Utc);
        await vm.WhenSettledAsync();

        Assert.Equal("2026-11-13 08:05", vm.TimeFromText);
        Assert.Empty(vm.View); // 08:05 UTC is after every event (the last is 02:50 UTC)
    }

    [Fact]
    public async Task Around_the_selected_event_uses_the_zone_shown()
    {
        var vm = Docs.Vm();
        vm.SetTimeDisplay(Utc);
        vm.SelectedEvent = vm.View[0];

        Assert.True(vm.SetAround(TimeSpan.FromSeconds(30)));
        await vm.WhenSettledAsync();

        Assert.Equal("2026-11-13 02:29:30.000", vm.TimeFromText);
        Assert.Single(vm.View);
    }

    [Fact]
    public void Go_to_a_time_uses_the_zone_shown()
    {
        var vm = Docs.Vm();
        vm.SetTimeDisplay(Utc);

        var (row, error) = vm.ResolveGoTo("2026-11-13 02:45");

        Assert.Null(error);
        Assert.Equal("Shutting down", vm.View[row].Message); // 08:20 as logged, the first at or after 02:45 UTC
    }

    [Fact]
    public void Setting_the_same_display_again_changes_nothing()
    {
        var vm = Docs.Vm();
        var changed = 0;
        vm.PropertyChanged += (_, _) => changed++;

        vm.SetTimeDisplay(new TimeDisplay());

        Assert.Equal(0, changed);
    }
}

/// <summary>Hide rules (FR-S6): left out of every log, with a one-click bypass.</summary>
public class HideRuleViewModelTests
{
    private static readonly HideRule NoDebug = new() { Name = "Debug noise", Query = "@l = 'Debug'" };

    private static readonly HideRule NoShipped = new() { Query = "@m like '%shipped'" };

    [Fact]
    public async Task Matching_events_are_left_out_until_the_rules_are_switched_off()
    {
        var vm = Docs.Vm();

        vm.SetHideRules([NoDebug, NoShipped]);
        await vm.WhenSettledAsync();

        Assert.Equal(3, vm.View.Count);
        Assert.True(vm.HasHideRules);
        Assert.True(vm.HasActiveFilters);
        Assert.Equal("Hiding noise (2 rules)", vm.HideRulesLabel);
        Assert.Equal("Debug noise\n@m like '%shipped'", vm.HideRulesList);

        vm.HideRulesOn = false;
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.View.Count);
        Assert.Equal("Noise shown (2 rules off)", vm.HideRulesLabel);
    }

    [Fact]
    public async Task Rules_that_are_off_or_can_not_be_read_are_ignored()
    {
        var vm = Docs.Vm();

        vm.SetHideRules([NoDebug with { Enabled = false }, new HideRule { Query = "@m like" }]);
        await vm.WhenSettledAsync();

        Assert.False(vm.HasHideRules);
        Assert.Equal(6, vm.View.Count);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task Clearing_the_filters_keeps_the_hide_rules()
    {
        var vm = Docs.Vm();
        vm.SetHideRules([NoDebug]);
        vm.QueryText = "@l = 'Error'";
        vm.ApplyQueryNow();
        await vm.WhenSettledAsync();

        vm.ClearFilters();
        await vm.WhenSettledAsync();

        Assert.Equal(5, vm.View.Count);
        Assert.DoesNotContain(vm.View, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task New_events_from_a_tail_pass_the_rules_too() => await UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        var stream = streams.Waits(@"C:\logs\tail.txt");
        var main = TestSupport.Main(streams);
        var open = main.OpenAsync([@"C:\logs\tail.txt"]);
        stream.Write(TestSupport.Line(1), initialLoadComplete: true);
        await open;
        var vm = main.CurrentLog!;
        vm.SetHideRules([NoDebug]);
        await vm.WhenSettledAsync();

        stream.Write(TestSupport.Line(2, "DBG") + TestSupport.Line(3, "INF"));
        stream.Write(TestSupport.Line(4));
        await Eventually.True(() => vm.Document.Events.Count >= 3, "the new events");
        await vm.WhenSettledAsync();

        Assert.DoesNotContain(vm.View, e => e.Level == LogLevel.Debug);
        Assert.Contains(vm.View, e => e.Message == "event 3");
    });
}

/// <summary>Saved filters (FR-S7): the query and the level choice, recalled by name.</summary>
public class SavedFilterViewModelTests
{
    [Fact]
    public async Task A_filter_is_captured_and_applied_again()
    {
        var vm = Docs.Vm();
        vm.MinLevelIndex = 3; // Warning and above
        vm.QueryText = "@m contains 'payment'";
        vm.ApplyQueryNow();
        await vm.WhenSettledAsync();

        var saved = vm.CaptureFilter("  Payment problems ");
        Assert.Equal("Payment problems", saved.Name);
        Assert.Equal("@m contains 'payment'", saved.Query);
        Assert.True(vm.Matches(saved));

        vm.ClearFilters();
        await vm.WhenSettledAsync();
        Assert.False(vm.Matches(saved));

        vm.ApplySavedFilter(saved);
        await vm.WhenSettledAsync();

        Assert.True(vm.Matches(saved));
        Assert.Equal(3, vm.MinLevelIndex);
        Assert.Equal(["Retry for payment", "Payment failed for order 2"], vm.View.Select(e => e.Message));
    }

    [Fact]
    public async Task A_filter_saved_in_another_log_works_with_the_levels_this_log_has()
    {
        var vm = Docs.Vm();
        var errorsOnly = new SavedFilter { Name = "Errors", Levels = LevelMask.With(0, LogLevel.Error, true) };

        vm.ApplySavedFilter(errorsOnly);
        await vm.WhenSettledAsync();

        Assert.Equal(["Payment failed for order 2"], vm.View.Select(e => e.Message));
        Assert.Equal(string.Empty, vm.QueryText);
        Assert.True(vm.Matches(errorsOnly));
    }
}

/// <summary>Tail by default (FR-P3).</summary>
public class TailByDefaultTests
{
    private const string Path1 = @"C:\logs\one.txt";

    [Fact]
    public Task Files_the_user_opens_start_tailing() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);
        vm.TailByDefault = true;

        await vm.OpenAsync([Path1]);

        Assert.True(vm.CurrentLog?.IsFollowing);
        Assert.True(streams.Opened.Single().Follow);
    });

    [Fact]
    public Task Without_it_files_open_without_tailing() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(Path1);
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([Path1]);

        Assert.False(vm.CurrentLog?.IsFollowing);
    });
}
