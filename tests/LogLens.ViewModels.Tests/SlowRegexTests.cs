using System.Diagnostics;
using LogLens.Core.Filtering;
using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.Settings;
using LogLens.Core.Streaming;

namespace LogLens.ViewModels.Tests;

/// <summary>NFR-10: a regular expression that is too slow on a log is rejected like an invalid one instead of stalling the tab (and its tail).</summary>
public class SlowRegexTests
{
    private const string Path = @"C:\logs\log20261113.txt";

    // Nested repeats behind a lookahead: only the backtracking engine can run it, and it times out on a long run of words.
    private const string Slow = @"(?=.)^(\w+\s?)+!$";

    private static readonly string LongWords = string.Join(" ", Enumerable.Repeat("words", 30));

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10); // hours before the fix

    private static string Log(int longEvents)
    {
        var text = Docs.Sample;
        for (var i = 0; i < longEvents; i++)
        {
            text += $"2026-11-13 08:30:{i % 60:00}.000 +05:30 [INF] {LongWords}\n";
        }

        return text;
    }

    private static LogViewModel Vm(int longEvents = 200) => new(TestSupport.Document(Log(longEvents), "sample.txt"));

    [Fact]
    public async Task Find_with_a_regex_too_slow_for_the_log_reports_it_and_counts_no_matches()
    {
        var vm = Vm();
        var clock = Stopwatch.StartNew();
        vm.FindBarVisible = true;
        vm.UseRegex = true;
        vm.SearchText = Slow;
        await vm.WhenSettledAsync();

        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
        Assert.Equal(UserRegex.TooSlowMessage, vm.SearchError);
        Assert.Null(vm.CurrentMatcher);
        Assert.Equal(0, vm.MatchCount);
    }

    [Fact]
    public async Task Only_matches_with_a_regex_too_slow_for_the_log_lists_every_event_again()
    {
        var vm = Vm();
        var total = vm.Document.Events.Count;
        vm.FindBarVisible = true;
        vm.UseRegex = true;
        vm.OnlyMatches = true;
        vm.SearchText = Slow;
        await vm.WhenSettledAsync();

        Assert.Equal(UserRegex.TooSlowMessage, vm.SearchError);
        Assert.Equal(total, vm.View.Count); // like an invalid pattern: not half filtered
    }

    [Fact]
    public async Task Find_with_nested_repeats_but_no_lookaround_works_on_the_same_log()
    {
        var vm = Vm();
        vm.FindBarVisible = true;
        vm.UseRegex = true;
        vm.SearchText = @"^(\w+\s?)+$";
        await vm.WhenSettledAsync();

        Assert.Null(vm.SearchError);
        Assert.Equal(206, vm.MatchCount); // the long events plus every sample message made only of words
    }

    [Fact]
    public async Task A_query_too_slow_for_the_log_keeps_the_last_valid_query_and_says_why()
    {
        var vm = Vm();
        vm.QueryText = "@l >= 'Error'";
        await vm.WhenSettledAsync();
        Assert.Equal(2, vm.View.Count);

        var clock = Stopwatch.StartNew();
        vm.QueryText = $"@m matches '{Slow}'";
        await vm.WhenSettledAsync();

        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
        Assert.Equal(UserRegex.TooSlowMessage, vm.QueryError);
        Assert.Equal(2, vm.View.Count); // still the last good filter

        vm.QueryText = "@m contains 'order'";
        await vm.WhenSettledAsync();

        Assert.False(vm.HasQueryError);
        Assert.Equal(3, vm.View.Count);
    }

    [Fact]
    public async Task A_hide_rule_too_slow_for_the_log_is_skipped_with_a_notice()
    {
        var vm = Vm();
        var total = vm.Document.Events.Count;
        var notices = new List<Notice>();
        vm.NoticeRaised += (_, n) => notices.Add(n);

        vm.SetHideRules([new HideRule { Name = "Shouting", Query = $"@m matches '{Slow}'" }, new HideRule { Query = "@l = 'Debug'" }]);
        await vm.WhenSettledAsync();

        var notice = Assert.Single(notices);
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.Contains("Shouting", notice.Message);
        Assert.Equal(total - 1, vm.View.Count); // the other rule still hides the debug event
    }

    [Fact]
    public async Task A_query_that_becomes_too_slow_on_tailed_events_is_dropped_without_stalling_the_tail()
    {
        var parser = LogFormats.SerilogText.CreateParser(Path);
        long bytes = 0;
        var vm = new LogViewModel(new LogDocument(Path, LogFormats.SerilogText)) { IsFollowing = true };
        void Send(string text, bool complete = false)
        {
            bytes += text.Length;
            vm.HandleBatch(new ChunkBatch(text, parser.Append(text), bytes, bytes, complete, Path, "UTF-8"));
        }

        Send(Docs.Sample, complete: true);
        vm.QueryText = $"@m matches '{Slow}'";
        await vm.WhenSettledAsync();
        Assert.Null(vm.QueryError); // fine on short messages
        Assert.Empty(vm.View);

        var clock = Stopwatch.StartNew();
        Send(Log(200)[Docs.Sample.Length..] + "2026-11-13 08:40:00.000 +05:30 [INF] last\n");
        await vm.WhenSettledAsync();

        Assert.True(clock.Elapsed < Budget, $"took {clock.Elapsed}");
        Assert.Equal(UserRegex.TooSlowMessage, vm.QueryError);
        Assert.Equal(vm.Document.Events.Count, vm.View.Count); // no query worked before it, so nothing is filtered
    }
}
