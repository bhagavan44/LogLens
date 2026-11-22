using LogLens.Core.Formats;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class SerilogTextFormatTests
{
    private static readonly string Sample = """
        # stray header line
        2026-11-13 08:00:00.012 +05:30 [INF] Application starting up
        2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed for order 67204
        System.InvalidOperationException: Sequence contains no matching element
           at System.Linq.ThrowHelper.ThrowNoMatchException()
        2026-11-13 08:00:06.177 +05:30 [INF] Message with an embedded newline:
        second line
        2026-11-13 08:00:07.512 +05:30 [WRN] Ünïcödé — 日本語 ✓
        2026-11-13 08:00:07.600 +05:30 [inf] last one
        """.ReplaceLineEndings("\n");

    [Fact]
    public void Registry_offers_serilog_text_as_the_only_and_default_format()
    {
        var format = Assert.Single(LogFormats.All);

        Assert.Same(LogFormats.SerilogText, format);
        Assert.Same(format, LogFormats.Default);
        Assert.Equal("serilog-text", format.Id);
        Assert.Equal("Serilog text", format.DisplayName);
        Assert.Equal(LogFields.Exception, format.Fields);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(10_000)]
    public void Feeding_text_in_chunks_gives_the_same_events_as_one_pass(int chunkSize)
    {
        var text = Sample.Replace("\n", "\r\n") + "\r\n";
        var expected = new PlainTextLogParser().Parse(text, "a.txt").Events;

        var document = new LogDocument("a.txt", LogFormats.SerilogText);
        var parser = LogFormats.SerilogText.CreateParser("a.txt");
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var piece = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            document.Text.Append(piece);
            document.Apply(parser.Append(piece));
        }

        document.Apply(parser.Complete());

        Assert.Equal(expected, document.Events);
    }

    [Fact]
    public void A_partial_last_line_is_held_back_until_its_newline_arrives()
    {
        var parser = new SerilogTextParser();

        var first = parser.Append("2026-11-13 08:00:00.012 +05:30 [INF] one\n2026-11-13 08:00:01.000 +05:30 [ERR] tw");

        Assert.True(parser.HasPartialLine);
        Assert.Equal("one", Assert.Single(first.Events).Message);

        var second = parser.Append("o\n");

        Assert.False(parser.HasPartialLine);
        Assert.Equal(0, second.FirstId); // "one" was still open (more continuation lines could follow), so it is re-sent
        Assert.Equal(["one", "two"], second.Events.Select(e => e.Message));
    }

    [Fact]
    public void Continuation_lines_in_a_later_chunk_replace_the_provisional_event()
    {
        var document = new LogDocument(null, LogFormats.SerilogText);
        var parser = LogFormats.SerilogText.CreateParser(null);

        void Feed(string text)
        {
            document.Text.Append(text);
            document.Apply(parser.Append(text));
        }

        Feed("2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed\n");
        Assert.Null(document.Events[0].Exception);

        Feed("System.InvalidOperationException: boom\n   at A.B()\n");
        document.Apply(parser.Complete());

        var only = Assert.Single(document.Events);
        Assert.Equal("System.InvalidOperationException: boom\n   at A.B()", only.Exception);
        Assert.Equal(1, document.CountOf(LogLevel.Error));
    }

    [Fact]
    public void Unicode_and_lowercase_levels_are_read()
    {
        var events = TestDocuments.FromText(Sample).Events;

        Assert.Contains(events, e => e.Message == "Ünïcödé — 日本語 ✓" && e.Level == LogLevel.Warning);
        Assert.Equal(LogLevel.Information, events[^1].Level);
    }

    [Fact]
    public void A_very_long_message_is_kept_whole()
    {
        var message = new string('x', 200_000);

        var e = Assert.Single(TestDocuments.FromText($"2026-11-13 08:00:00.012 +05:30 [INF] {message}\n").Events);

        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void A_file_with_no_matching_line_is_all_unparsed_and_nothing_is_dropped()
    {
        var document = TestDocuments.FromText("just some text\n\nanother line\n");

        Assert.Equal(2, document.Events.Count);
        Assert.Equal(2, document.UnparsedCount);
        Assert.All(document.Events, e => Assert.True(e.IsUnparsed));
    }
}
