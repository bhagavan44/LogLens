using LogLens.Core.Formats;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class TextStoreTests
{
    [Fact]
    public void Substring_stitches_ranges_across_chunks()
    {
        var store = new TextStore();
        var big = new string('a', 300 * 1024); // larger than the merge limit, so it starts a chunk of its own
        store.Append("hello ");
        store.Append(big);
        store.Append("world");

        Assert.Equal(3, store.ChunkCount); // "world" no longer fits in the big chunk either
        Assert.Equal(6 + big.Length + 5, store.Length);
        Assert.Equal("hello aaa", store.Substring(0, 9));
        Assert.Equal("aaworld", store.Substring(store.Length - 7, 7));
        Assert.Equal(string.Empty, store.Substring(3, 0));
    }

    [Fact]
    public void Substring_outside_the_text_throws()
    {
        var store = new TextStore();
        store.Append("abc");

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Substring(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Substring(-1, 1));
    }

    [Fact]
    public void Line_numbers_follow_newlines_across_chunks_and_appends()
    {
        var store = new TextStore();
        store.Append("one\r\ntwo\n");
        store.Append(new string('x', 300 * 1024) + "\nfour");

        Assert.Equal(1, store.LineNumberAt(0));
        Assert.Equal(1, store.LineNumberAt(3));
        Assert.Equal(2, store.LineNumberAt(5));
        Assert.Equal(3, store.LineNumberAt(9));
        Assert.Equal(4, store.LineNumberAt(store.Length - 1));

        store.Append("\nfive");

        Assert.Equal(5, store.LineNumberAt(store.Length - 1)); // the index is rebuilt after an append
    }
}

public class LogDocumentTests
{
    private const string Text = """
        2026-11-13 08:00:00.012 +05:30 [INF] one
        2026-11-13 08:00:05.301 +05:30 [ERR] two
        System.Exception: boom
           at A.B()
        2026-11-13 08:00:06.000 +05:30 [ERR] three
        """;

    [Fact]
    public void Counts_levels_and_reports_raw_text_and_line_numbers()
    {
        var document = TestDocuments.FromText(Text, @"C:\logs\app.txt");

        Assert.Equal("app.txt", document.FileName);
        Assert.Same(LogFormats.SerilogText, document.Format);
        Assert.Equal(3, document.Events.Count);
        Assert.Equal(1, document.CountOf(LogLevel.Information));
        Assert.Equal(2, document.CountOf(LogLevel.Error));
        Assert.Equal(0, document.UnparsedCount);
        Assert.Equal("2026-11-13 08:00:05.301 +05:30 [ERR] two\nSystem.Exception: boom\n   at A.B()", document.GetRaw(document.Events[1]));
        Assert.Equal([1, 2, 5], document.Events.Select(document.LineNumberOf));
    }

    [Fact]
    public void Apply_rejects_a_delta_that_does_not_fit()
    {
        var document = TestDocuments.FromText(Text);
        var stray = new LogEvent(7, null, 0, 0, default, LogLevel.Information, "x", null);

        Assert.Throws<InvalidOperationException>(() => document.Apply(new ParseDelta(9, [])));
        Assert.Throws<InvalidOperationException>(() => document.Apply(new ParseDelta(1, [stray])));
    }

    [Fact]
    public void Apply_replaces_events_from_the_first_id_and_keeps_counts_right()
    {
        var document = TestDocuments.FromText(Text);
        var replacement = new LogEvent(1, null, 0, 0, default, LogLevel.Warning, "replaced", null);

        document.Apply(new ParseDelta(1, [replacement]));

        Assert.Equal(2, document.Events.Count);
        Assert.Equal(0, document.CountOf(LogLevel.Error));
        Assert.Equal(1, document.CountOf(LogLevel.Warning));
    }
}

public class LogTextTests
{
    private static readonly LogEvent Event = new(0, null, 0, 0, new DateTimeOffset(2026, 11, 13, 8, 0, 5, 301, TimeSpan.FromMinutes(330)), LogLevel.Error, "first\nsecond", null);

    [Fact]
    public void Timestamps_are_shown_as_logged()
    {
        Assert.Equal("2026-11-13 08:00:05.301", LogText.Time(Event));
        Assert.Equal("2026-11-13 08:00:05.301 +05:30", LogText.FullTime(Event));
    }

    [Fact]
    public void Unparsed_events_have_no_timestamp()
    {
        var unparsed = Event with { Level = LogLevel.Unparsed };

        Assert.Equal("—", LogText.Time(unparsed));
        Assert.Equal("no timestamp", LogText.FullTime(unparsed));
    }

    [Theory]
    [InlineData(LogLevel.Verbose, "VRB", "Verbose")]
    [InlineData(LogLevel.Debug, "DBG", "Debug")]
    [InlineData(LogLevel.Information, "INF", "Information")]
    [InlineData(LogLevel.Warning, "WRN", "Warning")]
    [InlineData(LogLevel.Error, "ERR", "Error")]
    [InlineData(LogLevel.Fatal, "FTL", "Fatal")]
    [InlineData(LogLevel.Unknown, "???", "Unknown level")]
    [InlineData(LogLevel.Unparsed, "—", "Unparsed")]
    public void Every_level_has_text(LogLevel level, string abbreviation, string name)
    {
        Assert.Equal(abbreviation, LogText.LevelAbbreviation(level));
        Assert.Equal(name, LogText.LevelName(level));
    }

    [Theory]
    [InlineData("single", "single")]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("", "")]
    public void First_line_is_what_the_grid_shows(string message, string expected)
        => Assert.Equal(expected, LogText.FirstLine(message));
}
