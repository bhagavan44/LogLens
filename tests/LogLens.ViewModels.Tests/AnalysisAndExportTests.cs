using System.Text;
using LogLens.Core.Export;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.ViewModels.Tests;

/// <summary>Newest first (FR-G3), the timeline (FR-N2), the statistics panel (FR-X1, FR-X2) and export (FR-E1).</summary>
public class NewestFirstViewModelTests
{
    [Fact]
    public void Newest_first_turns_the_rows_round_and_keeps_the_selected_event()
    {
        var vm = Docs.Vm();
        vm.SelectedEvent = vm.Events[2];
        LogEvent? seen = null;
        vm.FilterApplied += (_, e) => seen = e;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NewestFirst = true;

        Assert.True(vm.NewestFirst);
        Assert.Equal("Shutting down", vm.Events[0].Message);
        Assert.Equal("Order 1 shipped", vm.Events[^1].Message);
        Assert.Same(vm.SelectedEvent, seen); // the grid keeps this event in sight
        Assert.Contains(nameof(LogViewModel.NewestFirst), changed);
        Assert.Contains(nameof(LogViewModel.Events), changed);
    }

    [Fact]
    public void Setting_the_same_order_again_does_nothing()
    {
        var vm = Docs.Vm();
        var raised = 0;
        vm.FilterApplied += (_, _) => raised++;

        vm.NewestFirst = false;
        vm.NewestFirst = true;
        vm.NewestFirst = true;

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Find_matches_are_counted_again_in_the_new_order()
    {
        var vm = Docs.Vm();
        vm.SearchText = "shipped";
        vm.FindBarVisible = true;
        await vm.WhenSettledAsync();
        Assert.Equal([0, 4], vm.StepsThroughMatches(2));

        vm.NewestFirst = true;
        await vm.WhenSettledAsync();

        Assert.Equal(2, vm.MatchCount);
        Assert.Equal([1, 5], vm.StepsThroughMatches(2)); // "Order 3 shipped" is row 1 now, "Order 1 shipped" row 5
        Assert.Equal("Order 3 shipped", vm.Events[1].Message);
    }

    [Fact]
    public void Bookmarks_and_go_to_use_the_rows_of_the_new_order()
    {
        var vm = Docs.Vm();
        vm.NewestFirst = true;

        Assert.Equal((5, null), vm.ResolveGoTo("1")); // event 1 is the last row
        var (row, error) = vm.ResolveGoTo("2026-11-13 08:10:00");
        Assert.Null(error);
        Assert.Equal("Order 3 shipped", vm.Events[row].Message);

        vm.ToggleBookmark(vm.Events[3]);
        Assert.Equal(3, vm.NextBookmarkRow(0, forward: true));
    }
}

public class AnalysisViewModelTests
{
    [Fact]
    public async Task Nothing_is_computed_until_the_panels_are_shown()
    {
        var vm = Docs.Vm();
        await vm.WhenSettledAsync();

        Assert.True(vm.Timeline.IsEmpty);
        Assert.Equal(0, vm.Statistics.Total);
        Assert.Equal("No events", vm.StatisticsSummary);
    }

    [Fact]
    public async Task Showing_the_statistics_summarises_the_listed_events()
    {
        var vm = Docs.Vm();

        vm.StatisticsVisible = true;
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.Statistics.Total);
        Assert.Equal("6 events  ·  2026-11-13 08:00:00.000 +05:30 → 08:20:00.000  (20 min)", vm.StatisticsSummary);
        Assert.Equal("1 fatal  ·  1 error  ·  1 warning  ·  2 information  ·  1 debug", vm.StatisticsLevelLine);
        Assert.Equal("Order {n} shipped", vm.Statistics.TopMessages[0].Key);
        Assert.False(vm.HasNoExceptionStats);
        Assert.Equal("System.InvalidOperationException: boom", vm.Statistics.TopExceptions[0].Key);
    }

    [Fact]
    public async Task The_statistics_follow_the_filters()
    {
        var vm = Docs.Vm();
        vm.StatisticsVisible = true;
        await vm.WhenSettledAsync();

        vm.LevelToggles.Single(t => t.Level == LogLevel.Information).IsOn = false;
        await vm.WhenSettledAsync();

        Assert.Equal(4, vm.Statistics.Total);
        Assert.Equal(0, vm.Statistics.LevelCounts[(int)LogLevel.Information]);
    }

    [Fact]
    public async Task The_statistics_are_computed_oldest_first_even_when_the_list_is_newest_first()
    {
        var vm = Docs.Vm();
        vm.NewestFirst = true;

        vm.StatisticsVisible = true;
        await vm.WhenSettledAsync();

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.FromMinutes(330)), vm.Statistics.First);
        Assert.Equal("Order 1 shipped", vm.Statistics.TopMessages[0].Sample);
    }

    [Fact]
    public async Task Clicking_a_statistics_row_filters_to_its_events()
    {
        var vm = Docs.Vm();
        vm.StatisticsVisible = true;
        await vm.WhenSettledAsync();

        vm.ShowOnlyMessage(vm.Statistics.TopMessages[0]);
        await vm.WhenSettledAsync();

        Assert.Equal(["Order 1 shipped", "Order 3 shipped"], vm.View.Select(e => e.Message));
        Assert.True(vm.HasActivePin);
        Assert.Equal(2, vm.Statistics.Total);
    }

    [Fact]
    public async Task Clicking_an_exception_row_filters_to_the_events_with_that_exception()
    {
        var vm = Docs.Vm();
        vm.StatisticsVisible = true;
        await vm.WhenSettledAsync();

        vm.ShowOnlyException(vm.Statistics.TopExceptions[0]);
        await vm.WhenSettledAsync();

        Assert.Equal(["Payment failed for order 2"], vm.View.Select(e => e.Message));
    }

    [Fact]
    public async Task The_timeline_counts_the_listed_events_by_level()
    {
        var vm = Docs.Vm();

        vm.TimelineVisible = true;
        await vm.WhenSettledAsync();

        Assert.Equal(6, vm.Timeline.EventCount);
        Assert.Equal(1, vm.Timeline.CountsByLevel[(int)LogLevel.Error].Sum());
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 0), vm.Timeline.Start);
    }

    [Fact]
    public async Task The_timeline_ignores_its_own_time_window_but_obeys_every_other_filter()
    {
        var vm = Docs.Vm();
        vm.TimelineVisible = true;
        await vm.WhenSettledAsync();

        vm.SelectTimeRange(new DateTime(2026, 11, 13, 8, 2, 0), new DateTime(2026, 11, 13, 8, 4, 0));
        await vm.WhenSettledAsync();

        Assert.Equal(2, vm.View.Count); // the error and the debug event
        Assert.Equal(6, vm.Timeline.EventCount); // the bars stay, so another range can still be picked
        Assert.True(vm.IsTimeFiltered);

        vm.LevelToggles.Single(t => t.Level == LogLevel.Fatal).IsOn = false;
        await vm.WhenSettledAsync();

        Assert.Equal(5, vm.Timeline.EventCount);
    }

    [Fact]
    public async Task A_range_picked_backwards_is_put_the_right_way_round()
    {
        var vm = Docs.Vm();

        vm.SelectTimeRange(new DateTime(2026, 11, 13, 8, 4, 0), new DateTime(2026, 11, 13, 8, 2, 0));
        await vm.WhenSettledAsync();

        Assert.Equal(new DateTime(2026, 11, 13, 8, 2, 0), vm.TimeWindow.From);
        Assert.Equal(new DateTime(2026, 11, 13, 8, 4, 0), vm.TimeWindow.To);
        Assert.Equal("2026-11-13 08:02:00.000", vm.TimeFromText);
    }
}

public class ExportViewModelTests
{
    private static async Task<string> ExportAsync(LogViewModel vm, ExportFormat format, IReadOnlyList<LogEvent>? selection = null)
    {
        using var stream = new MemoryStream();
        await vm.ExportAsync(format, stream, selection);
        return new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('﻿');
    }

    [Fact]
    public async Task The_listed_events_are_exported_in_file_order()
    {
        var vm = Docs.Vm();
        vm.LevelToggles.Single(t => t.Level == LogLevel.Information).IsOn = false;
        await vm.WhenSettledAsync();

        var csv = await ExportAsync(vm, ExportFormat.Csv);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + vm.View.Count, lines.Length);
        Assert.Contains("Retry for payment", lines[1]);
        Assert.Contains("Shutting down", lines[^1]);
    }

    [Fact]
    public async Task A_newest_first_list_is_still_written_oldest_first()
    {
        var vm = Docs.Vm();
        vm.NewestFirst = true;

        var text = await ExportAsync(vm, ExportFormat.Text);

        Assert.StartsWith("2026-11-13 08:00:00.000 +05:30 [INF] Order 1 shipped", text);
        Assert.EndsWith("[FTL] Shutting down\r\n", text);
    }

    [Fact]
    public async Task Only_the_selection_is_exported_when_one_is_given_and_in_file_order()
    {
        var vm = Docs.Vm();
        vm.NewestFirst = true;

        // Rows as the grid lists them: newest first.
        var json = await ExportAsync(vm, ExportFormat.Json, [vm.Events[0], vm.Events[5]]);

        var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Order 1 shipped", lines[0]);
        Assert.Contains("Shutting down", lines[1]);
    }

    [Fact]
    public async Task The_html_export_names_the_file_and_says_what_was_filtered()
    {
        var vm = Docs.Vm();
        vm.MinLevelIndex = 3;
        await vm.WhenSettledAsync();

        var html = await ExportAsync(vm, ExportFormat.Html);

        Assert.Contains("<title>sample.txt</title>", html);
        Assert.Contains("3 of 6 events (filtered)", html);
    }

    [Fact]
    public async Task Exporting_leaves_the_stream_open_and_reports_the_count()
    {
        var vm = Docs.Vm();
        using var stream = new MemoryStream();

        var written = await vm.ExportAsync(ExportFormat.Markdown, stream);

        Assert.Equal(6, written);
        Assert.True(stream.CanWrite);
        Assert.Equal(6, vm.ExportableCount());
        Assert.Equal(2, vm.ExportableCount([vm.Events[0], vm.Events[1]]));
    }

    [Fact]
    public async Task Markdown_for_the_clipboard_is_ordered_by_event_and_has_no_filter_text()
    {
        var vm = Docs.Vm();
        vm.NewestFirst = true;

        var markdown = await vm.BuildMarkdownAsync([vm.Events[0], vm.Events[1]]);

        var lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains("Order 3 shipped", lines[2]);
        Assert.Contains("Shutting down", lines[3]);
    }

    [Theory]
    [InlineData(ExportFormat.Csv, ".csv")]
    [InlineData(ExportFormat.Json, ".jsonl")]
    [InlineData(ExportFormat.Html, ".html")]
    [InlineData(ExportFormat.Text, ".txt")]
    [InlineData(ExportFormat.Markdown, ".md")]
    public void The_suggested_name_follows_the_log_and_what_is_exported(ExportFormat format, string extension)
    {
        var vm = Docs.Vm();

        Assert.Equal("sample" + extension, vm.SuggestedExportName(format, selectionOnly: false));
        Assert.Equal("sample-selection" + extension, vm.SuggestedExportName(format, selectionOnly: true));
    }

    [Fact]
    public async Task The_suggested_name_says_when_the_list_is_filtered()
    {
        var vm = Docs.Vm();
        vm.MinLevelIndex = 3;
        await vm.WhenSettledAsync();

        Assert.Equal("sample-filtered.csv", vm.SuggestedExportName(ExportFormat.Csv, selectionOnly: false));
    }
}

internal static class MatchSteps
{
    /// <summary>The rows F3 visits, starting from the top and following the matches round.</summary>
    public static int[] StepsThroughMatches(this LogViewModel vm, int count)
    {
        var rows = new int[count];
        var from = -1;
        for (var i = 0; i < count; i++)
        {
            from = vm.StepMatch(from, forward: true);
            rows[i] = from;
        }

        return rows;
    }
}
