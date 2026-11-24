using System.Diagnostics;
using LogLens.Core.Collections;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class QueryTests
{
    private static Func<LogEvent, bool> Compile(string text)
    {
        Assert.True(QueryParser.TryParse(text, out var query, out var error), $"'{text}' failed: {error.Message}");
        return query!.Predicate;
    }

    private static QueryError Fail(string text)
    {
        Assert.False(QueryParser.TryParse(text, out var query, out var error), $"'{text}' unexpectedly compiled");
        Assert.Null(query);
        return error;
    }

    private static readonly LogEvent Info = Ev.Make(LogLevel.Information, "Order 42 shipped", time: "2026-11-13 08:00:00");
    private static readonly LogEvent Warn = Ev.Make(LogLevel.Warning, "Retry 2/3 for payment gateway", time: "2026-11-13 08:05:00");
    private static readonly LogEvent Error = Ev.Make(LogLevel.Error, "Payment failed", "System.InvalidOperationException: boom\n   at A.B()", time: "2026-11-13 09:30:00");
    private static readonly LogEvent Fatal = Ev.Make(LogLevel.Fatal, "Shutting down", "System.OutOfMemoryException: oom", time: "2026-11-14 00:00:01.500");
    private static readonly LogEvent Junk = Ev.Make(message: "stray", unparsed: true);

    // ----- levels -----

    [Fact]
    public void Level_equality_and_in()
    {
        Assert.True(Compile("@l = 'Error'")(Error));
        Assert.False(Compile("@l = 'Error'")(Warn));
        Assert.True(Compile("@l != 'Error'")(Warn));
        Assert.True(Compile("@l in ['Error', 'Fatal']")(Fatal));
        Assert.False(Compile("@l in ['Error', 'Fatal']")(Info));
    }

    [Theory]
    [InlineData("@l >= 'Warning'", false, true, true, true)]
    [InlineData("@l > 'Warning'", false, false, true, true)]
    [InlineData("@l < 'Warning'", true, false, false, false)]
    [InlineData("@l <= 'Warning'", true, true, false, false)]
    public void Levels_compare_by_severity(string query, bool info, bool warn, bool error, bool fatal)
    {
        var p = Compile(query);

        Assert.Equal(info, p(Info));
        Assert.Equal(warn, p(Warn));
        Assert.Equal(error, p(Error));
        Assert.Equal(fatal, p(Fatal));
    }

    [Fact]
    public void Level_names_accept_abbreviations_and_any_case_and_numbers()
    {
        Assert.True(Compile("@l = 'err'")(Error));
        Assert.True(Compile("@l = 'WRN'")(Warn));
        Assert.True(Compile("@l >= 4")(Warn));
        Assert.True(Compile("@l = 6")(Fatal));
    }

    // ----- text -----

    [Fact]
    public void Text_operators_ignore_case()
    {
        Assert.True(Compile("@m contains 'PAYMENT'")(Warn));
        Assert.True(Compile("@m startswith 'order'")(Info));
        Assert.True(Compile("@m endswith 'FAILED'")(Error));
        Assert.True(Compile("@m = 'payment failed'")(Error));
        Assert.True(Compile("@m != 'x'")(Error));
        Assert.False(Compile("@m contains 'zzz'")(Error));
    }

    [Fact]
    public void Like_uses_sql_wildcards()
    {
        Assert.True(Compile("@m like '%payment%'")(Warn));
        Assert.True(Compile("@m like 'Order % shipped'")(Info));
        Assert.True(Compile("@m like 'Order _2 shipped'")(Info));
        Assert.False(Compile("@m like 'Order'")(Info));
        Assert.True(Compile("@m like 'Retry 2/3%'")(Warn)); // regex metacharacters are literal
    }

    [Fact]
    public void Matches_uses_a_regular_expression()
    {
        Assert.True(Compile(@"@m matches 'order \d+ shipped'")(Info));
        Assert.False(Compile(@"@m matches '^shipped'")(Info));
    }

    [Fact]
    public void In_works_for_text()
    {
        Assert.True(Compile("@m in ['payment failed', 'x']")(Error));
        Assert.False(Compile("@m in ['x', 'y']")(Error));
    }

    [Fact]
    public void Doubled_quotes_are_literal_quotes_and_double_quotes_work_too()
    {
        var quoted = Ev.Make(message: "it's \"fine\"");

        Assert.True(Compile("@m = 'it''s \"fine\"'")(quoted));
        Assert.True(Compile("@m contains \"it's\"")(quoted));
    }

    // ----- exception -----

    [Fact]
    public void Exception_field_and_exists()
    {
        Assert.True(Compile("exists(@x)")(Error));
        Assert.False(Compile("exists(@x)")(Info));
        Assert.True(Compile("@x = null")(Info));
        Assert.True(Compile("@x != null")(Error));
        Assert.True(Compile("@x contains 'InvalidOperation'")(Error));
        Assert.False(Compile("@x contains 'InvalidOperation'")(Info)); // null never matches
        Assert.True(Compile("not exists(@x)")(Info));
    }

    // ----- timestamps -----

    [Fact]
    public void Timestamps_compare_as_wall_clock_time()
    {
        Assert.True(Compile("@t >= '2026-11-13 08:05'")(Warn));
        Assert.False(Compile("@t >= '2026-11-13 08:05'")(Info));
        Assert.True(Compile("@t < '2026-11-13 09:00'")(Warn));
        Assert.True(Compile("@t >= '2026-11-14'")(Fatal));
        Assert.True(Compile("@t = '2026-11-14 00:00:01.5'")(Fatal));
    }

    [Fact]
    public void Time_ranges_combine_with_and()
    {
        var p = Compile("@t >= '2026-11-13 08:01' and @t <= '2026-11-13 09:30'");

        Assert.False(p(Info));
        Assert.True(p(Warn));
        Assert.True(p(Error));
        Assert.False(p(Fatal));
    }

    [Fact]
    public void Timestamps_without_an_offset_compare_with_the_time_as_logged()
    {
        // Info is 08:00 IST = 02:30 UTC; the grid shows 08:00.
        Assert.True(Compile("@t = '2026-11-13 08:00'")(Info));
        Assert.False(Compile("@t = '2026-11-13 02:30'")(Info));
    }

    [Fact]
    public void Timestamps_with_an_offset_are_exact_instants()
    {
        Assert.True(Compile("@t = '2026-11-13T02:30:00Z'")(Info));
        Assert.True(Compile("@t >= '2026-11-13T08:00:00+05:30'")(Info));
        Assert.False(Compile("@t > '2026-11-13T08:00:00+05:30'")(Info));
    }

    [Fact]
    public void Unparsed_lines_never_match_a_time_comparison()
    {
        Assert.False(Compile("@t >= '2000-01-01'")(Junk));
        Assert.False(Compile("@t != '2000-01-01'")(Junk));
        Assert.False(Compile("exists(@t)")(Junk));
    }

    [Fact]
    public void Lines_without_a_severity_never_match_a_level_comparison()
    {
        var unknown = Ev.Make(LogLevel.Unknown, "odd level");

        foreach (var query in new[] { "@l >= 'Verbose'", "@l < 'Fatal'", "@l != 'Error'", "exists(@l)" })
        {
            Assert.False(Compile(query)(Junk), query);
            Assert.False(Compile(query)(unknown), query);
        }

        Assert.True(Compile("not exists(@l)")(Junk));
    }

    // ----- boolean logic -----

    [Fact]
    public void And_binds_tighter_than_or_and_parentheses_override()
    {
        // a or (b and c)
        var p = Compile("@l = 'Fatal' or @l = 'Error' and @m contains 'payment'");
        Assert.True(p(Fatal));
        Assert.True(p(Error));
        Assert.False(p(Warn));

        // (a or b) and c
        var q = Compile("(@l = 'Fatal' or @l = 'Error') and @m contains 'payment'");
        Assert.False(q(Fatal));
        Assert.True(q(Error));
    }

    [Fact]
    public void Not_negates_terms_and_groups_and_infix_forms()
    {
        Assert.True(Compile("not @l = 'Error'")(Info));
        Assert.True(Compile("not (@l = 'Error' or @l = 'Fatal')")(Warn));
        Assert.True(Compile("@m not like '%payment%'")(Info));
        Assert.False(Compile("@m not like '%payment%'")(Warn));
        Assert.True(Compile("@l not in ['Error', 'Fatal']")(Info));
        Assert.True(Compile("@m not contains 'zzz'")(Info));
        Assert.True(Compile("not not @l = 'Error'")(Error));
    }

    [Fact]
    public void Keywords_and_fields_are_case_insensitive_and_aliases_work()
    {
        Assert.True(Compile("@L = 'Error' AND @M CONTAINS 'payment'")(Error));
        Assert.True(Compile("@level = 'Error' and @message contains 'payment' and @exception != null")(Error));
        Assert.True(Compile("@timestamp >= '2026-11-13'")(Error));
        Assert.True(Compile("@l == 'Error'")(Error));
        Assert.True(Compile("@l <> 'Error'")(Info));
    }

    [Fact]
    public void Spec_example_compiles_and_works()
    {
        var p = Compile("@l in ['Error','Fatal'] and @m like '%Payment%' and @t >= '2026-11-13 09:00'");

        Assert.True(p(Error));
        Assert.False(p(Fatal));
        Assert.False(p(Warn));
    }

    // ----- errors -----

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("@l =", "Expected a value")]
    [InlineData("@l = Error", "Put text in quotes")]
    [InlineData("@l = 'Nope'", "isn't a level")]
    [InlineData("@l = 9", "1 (Verbose) to 6")]
    [InlineData("@l like 'x'", "text fields")]
    [InlineData("@m > 'x'", "can't be used on text")]
    [InlineData("@m = 5", "text in quotes")]
    [InlineData("@t >= 'yesterday'", "isn't a timestamp")]
    [InlineData("@t >= '08:00'", "Include the date")]
    [InlineData("@t in ['2026-11-13']", "can't be used with @t")]
    [InlineData("@l = 'Error' @m = 'x'", "forget 'and'")]
    [InlineData("(@l = 'Error'", "Missing ')'")]
    [InlineData("@l in ['Error'", "Missing ']'")]
    [InlineData("@l in []", "empty")]
    [InlineData("@l in 'Error'", "list")]
    [InlineData("@m = 'unterminated", "closing")]
    [InlineData("@m matches '('", "regular expression")]
    [InlineData("@zzz = 'x'", "Unknown field")]
    [InlineData("RequestId", "expected a comparison")]
    [InlineData("and = 'x'", "Expected a field")]
    [InlineData("@p[RequestId] = 1", "in quotes")]
    [InlineData("@p['x' = 1", "Missing ']'")]
    [InlineData("Ok > true", "true and false")]
    [InlineData("Ok < null", "Only = and !=")]
    [InlineData("@l = 'Error' and", "Expected a field")]
    [InlineData("@l ! 'Error'", "Unexpected '!'")]
    [InlineData("@l = 'Error' ; x", "Unexpected character")]
    [InlineData("@x < null", "Only = and !=")]
    public void Invalid_queries_give_a_helpful_message(string query, string expectedFragment)
    {
        var error = Fail(query);

        Assert.Contains(expectedFragment, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Errors_point_at_the_offending_text()
    {
        var error = Fail("@l = 'Error' and @m = 5");

        Assert.Equal("@l = 'Error' and @m = ".Length, error.Position);
        Assert.Equal(1, error.Length);
    }

    [Fact]
    public void Unknown_level_error_covers_the_quoted_text()
    {
        var error = Fail("@l = 'Nope'");

        Assert.Equal(5, error.Position);
        Assert.Equal("'Nope'".Length, error.Length);
    }

    [Fact]
    public void Null_text_is_treated_as_empty()
    {
        Assert.False(QueryParser.TryParse(null, out _, out var error));
        Assert.Contains("empty", error.Message);
    }

    // ----- performance -----

    [Fact]
    public void A_compound_query_over_250k_events_is_fast()
    {
        var events = TestDocuments.Synthetic(250_000);
        var p = Compile("@l >= 'Warning' and (@m like '%payment%' or @m contains 'order') and not exists(@x) and @t >= '2026-11-13 08:30'");

        var clock = Stopwatch.StartNew();
        var hits = LogEventView.ComputeIndices(events, p);
        clock.Stop();

        Assert.NotEmpty(hits);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.ElapsedMilliseconds} ms");
        Console.WriteLine($"QUERY 250k events in {clock.ElapsedMilliseconds} ms ({hits.Length} hits)");
    }
}
