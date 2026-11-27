using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LogLens.Core.Filtering;
using LogLens.Core.Formats.Text;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

/// <summary>NFR-10: a regular expression the user types can never stall a pass over a log.</summary>
public class UserRegexTests
{
    // Nested repeats that backtrack catastrophically when the text doesn't end in '!'.
    internal const string Catastrophic = @"^(\w+\s?)+!$";

    // The same with a lookahead, which the linear-time engine can't run: it falls back to the backtracking engine and times out.
    internal const string CatastrophicWithLookahead = @"(?=.)^(\w+\s?)+!$";

    // Thirty words: far too many ways to split for a backtracking engine to finish within the timeout.
    internal static readonly string LongWords = string.Join(" ", Enumerable.Repeat("words", 30));

    private static LogEvent Event(int id, string message)
        => new(id, null, 0, message.Length, new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero).AddSeconds(id), LogLevel.Information, message, null);

    [Fact]
    public void A_pattern_the_linear_engine_can_run_never_backtracks_catastrophically()
    {
        var regex = UserRegex.Create(Catastrophic, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(regex.IsMatch(LongWords));
        }

        Assert.True(regex.IsLinear);
        Assert.False(regex.TooSlow);
        Assert.True(regex.IsMatch(LongWords + "!"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_pattern_with_a_lookaround_falls_back_to_the_backtracking_engine_and_still_matches()
    {
        var regex = UserRegex.Create(@"(?<!\w)order(?!\w)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        Assert.False(regex.IsLinear);
        Assert.True(regex.IsMatch("Order 42 shipped"));
        Assert.False(regex.IsMatch("Reorder 42"));
    }

    [Fact]
    public void An_invalid_pattern_is_an_argument_error()
    {
        Assert.ThrowsAny<ArgumentException>(() => UserRegex.Create("(unclosed", RegexOptions.None));
    }

    [Fact]
    public void A_backtracking_pattern_that_keeps_timing_out_gives_up_and_then_matches_nothing_at_once()
    {
        var regex = UserRegex.Create(CatastrophicWithLookahead, RegexOptions.CultureInvariant);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(regex.IsMatch(LongWords));
        }

        Assert.True(regex.TooSlow);
        Assert.False(regex.IsMatch(LongWords + "!")); // gave up: matches nothing any more
        Assert.Null(regex.Match(LongWords + "!", 0, LongWords.Length + 1));

        // Three timeouts, then nothing: well under a second per thousand events rather than minutes.
        var budget = UserRegex.MatchTimeout * (UserRegex.MaxTimeouts + 4);
        Assert.True(clock.Elapsed < budget, $"took {clock.Elapsed}");
    }

    [Fact]
    public void Find_with_a_catastrophic_regex_gives_up_instead_of_stalling()
    {
        var matcher = new SearchSpec(CatastrophicWithLookahead, UseRegex: true).TryCompile(out var error)!;
        Assert.Null(error);

        var clock = Stopwatch.StartNew();
        var matches = Enumerable.Range(0, 1000).Count(i => matcher.IsMatch(Event(i, LongWords)));

        Assert.Equal(0, matches);
        Assert.True(matcher.TooSlow);
        Assert.Empty(matcher.FindRanges(LongWords + "!"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Find_with_nested_repeats_but_no_lookaround_runs_linearly_and_finds_matches()
    {
        var matcher = new SearchSpec(Catastrophic, UseRegex: true).TryCompile(out _)!;

        var clock = Stopwatch.StartNew();
        var matches = Enumerable.Range(0, 1000).Count(i => matcher.IsMatch(Event(i, i % 2 == 0 ? LongWords : LongWords + "!")));

        Assert.Equal(500, matches);
        Assert.False(matcher.TooSlow);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_query_whose_regex_gives_up_says_so()
    {
        Assert.True(QueryParser.TryParse($"@m matches '{CatastrophicWithLookahead}'", out var slow, out _));
        Assert.True(QueryParser.TryParse($"@m matches '{Catastrophic}' or @m like '%words%words%words%'", out var fine, out _));

        for (var i = 0; i < 1000; i++)
        {
            slow!.Predicate(Event(i, LongWords));
            fine!.Predicate(Event(i, LongWords));
        }

        Assert.True(slow!.TooSlow);
        Assert.False(fine!.TooSlow);
        Assert.True(fine.Predicate(Event(0, LongWords)));
    }

    [Fact]
    public void A_query_without_regular_expressions_is_never_too_slow()
    {
        Assert.True(QueryParser.TryParse("@m contains 'x' and @l >= 'Warning'", out var query, out _));
        Assert.False(query!.TooSlow);
    }

    [Fact]
    public void A_custom_format_whose_regex_keeps_timing_out_stops_trying_it_instead_of_stalling_the_load()
    {
        var pattern = RegexLinePattern.Parse(@"(?<timestamp>\S+) (?=.)(?<message>(\w+\s?)+)!$");
        var text = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            text.Append("2026-11-13T08:00:00 ").Append(LongWords).Append('\n');
        }

        var parser = new PlainTextLogParser(pattern);
        var clock = Stopwatch.StartNew();
        var result = parser.Parse(text.ToString());

        Assert.True(parser.PatternTooSlow);
        Assert.NotEmpty(result.Events); // the text is still shown, never dropped (FR-F5)
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_custom_format_with_nested_repeats_but_no_lookaround_reads_every_line()
    {
        var pattern = RegexLinePattern.Parse(@"(?<timestamp>\S+) (?<message>(\w+\s?)+)!$");
        var text = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            text.Append("2026-11-13T08:00:00 ").Append(LongWords).Append(i % 2 == 0 ? "!" : string.Empty).Append('\n');
        }

        var parser = new PlainTextLogParser(pattern);
        var clock = Stopwatch.StartNew();
        var result = parser.Parse(text.ToString());

        Assert.False(parser.PatternTooSlow);
        Assert.Equal(1000, result.Events.Length); // each line without '!' continues the event before it (FR-F3)
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }
}
