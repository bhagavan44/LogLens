using System.Diagnostics;
using System.Text;
using LogLens.Core.Models;
using LogLens.Core.Formats.SerilogText;

namespace LogLens.Core.Tests;

public class PlainTextLogParserTests
{
    private static ParseResult Parse(string text, OutputTemplate? template = null)
        => new PlainTextLogParser(template).Parse(text, "test.txt");

    private static string Line(string time, string level, string message)
        => $"2026-11-13 {time} +05:30 [{level}] {message}";

    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void Empty_text_yields_no_events()
    {
        var result = Parse(string.Empty);

        Assert.Empty(result.Events);
        Assert.Equal(0, result.UnparsedCount);
    }

    [Fact]
    public void Single_line_events_are_parsed()
    {
        var result = Parse(Join(
            Line("08:00:00.012", "INF", "Application starting up"),
            Line("08:00:00.348", "DBG", "Loaded configuration"),
            Line("08:00:01.120", "WRN", "Disk almost full")));

        Assert.Equal(3, result.Events.Length);
        Assert.Equal([LogLevel.Information, LogLevel.Debug, LogLevel.Warning], result.Events.Select(e => e.Level));
        Assert.Equal("Application starting up", result.Events[0].Message);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 0, 0, 12, TimeSpan.FromMinutes(330)), result.Events[0].Timestamp);
        Assert.Equal([0, 1, 2], result.Events.Select(e => e.Id));
        Assert.All(result.Events, e => Assert.Equal("test.txt", e.SourceFile));
        Assert.All(result.Events, e => Assert.Null(e.Exception));
    }

    [Fact]
    public void Exception_text_is_split_from_the_message()
    {
        var text = Join(
            Line("08:00:05.301", "ERR", "Payment failed for order 67204"),
            "System.InvalidOperationException: Sequence contains no matching element",
            "   at System.Linq.ThrowHelper.ThrowNoMatchException()",
            "   at Shop.Payments.PaymentService.Charge(Order order) in C:\\src\\PaymentService.cs:line 87",
            Line("08:00:05.601", "INF", "Next event"));

        var result = Parse(text);

        Assert.Equal(2, result.Events.Length);
        var error = result.Events[0];
        Assert.Equal("Payment failed for order 67204", error.Message);
        Assert.StartsWith("System.InvalidOperationException: Sequence contains", error.Exception);
        Assert.EndsWith("PaymentService.cs:line 87", error.Exception);
        Assert.Equal(3, error.Exception!.Split('\n').Length);
        Assert.Null(result.Events[1].Exception);
    }

    [Fact]
    public void Multi_line_message_without_an_exception_stays_in_the_message()
    {
        var text = Join(
            Line("08:00:06.177", "INF", "Message with an embedded newline:"),
            "this second line is part of the same event",
            "and so is this third line",
            Line("08:00:06.251", "DBG", "after"));

        var result = Parse(text);

        Assert.Equal(2, result.Events.Length);
        Assert.Equal("Message with an embedded newline:\nthis second line is part of the same event\nand so is this third line", result.Events[0].Message);
        Assert.Null(result.Events[0].Exception);
    }

    [Fact]
    public void Message_continuation_then_exception_are_separated()
    {
        var text = Join(
            Line("08:00:07.004", "ERR", "Failed to handle message"),
            "payload was: {\"id\": 7}",
            "Newtonsoft.Json.JsonReaderException: Unexpected character",
            " ---> System.FormatException: Input string was not in a correct format.",
            "   at Shop.Deserializer.Read(String body)");

        var e = Parse(text).Events.Single();

        Assert.Equal("Failed to handle message\npayload was: {\"id\": 7}", e.Message);
        Assert.StartsWith("Newtonsoft.Json.JsonReaderException", e.Exception);
        Assert.Contains("---> System.FormatException", e.Exception);
        Assert.Contains("Deserializer.Read", e.Exception);
    }

    [Theory]
    [InlineData("System.Exception: x", true)]
    [InlineData("ArgumentNullException (Parameter 'x')", true)]
    [InlineData("Microsoft.Data.SqlClient.SqlException (0x80131904): timeout", true)]
    [InlineData("System.AggregateException: One or more errors occurred.", true)]
    [InlineData("   at Foo.Bar(String x)", true)]
    [InlineData("Error: disk full", false)]
    [InlineData("Exception: handled elsewhere", false)]
    [InlineData("this second line is part of the same event", false)]
    [InlineData("   at least five items were expected", false)]
    [InlineData("payload was: {\"id\": 7}", false)]
    [InlineData("", false)]
    public void Exception_start_detection(string line, bool expected)
        => Assert.Equal(expected, PlainTextLogParser.IsExceptionStart(line));

    [Fact]
    public void Stack_frames_without_a_header_line_are_still_an_exception()
    {
        var text = Join(
            Line("08:00:07.004", "ERR", "Boom"),
            "   at Shop.Thing.Run() in C:\\src\\Thing.cs:line 5");

        var e = Parse(text).Events.Single();

        Assert.Equal("Boom", e.Message);
        Assert.Contains("Thing.Run", e.Exception);
    }

    [Fact]
    public void Text_before_the_first_event_is_kept_as_unparsed()
    {
        var text = Join(
            "# log header written by something else",
            "second stray line",
            Line("08:00:00.012", "INF", "first real event"));

        var result = Parse(text);

        Assert.Equal(3, result.Events.Length);
        Assert.Equal(2, result.UnparsedCount);
        Assert.All(result.Events.Take(2), e =>
        {
            Assert.True(e.IsUnparsed);
            Assert.Equal(LogLevel.Unparsed, e.Level);
        });
        Assert.Equal("# log header written by something else", result.Events[0].Message);
        Assert.False(result.Events[2].IsUnparsed);
    }

    [Fact]
    public void Line_with_an_impossible_timestamp_is_not_an_event_start()
    {
        var text = Join(
            Line("08:00:00.012", "INF", "real"),
            "2026-02-31 08:00:00.012 +05:30 [INF] looks like an event but isn't");

        var result = Parse(text);

        var only = Assert.Single(result.Events);
        Assert.Contains("looks like an event but isn't", only.Message);
    }

    [Fact]
    public void Unknown_level_text_is_kept_as_Unknown_level()
    {
        var e = Parse(Join(Line("08:00:00.012", "XYZ", "odd level"))).Events.Single();

        Assert.Equal(LogLevel.Unknown, e.Level);
        Assert.False(e.IsUnparsed);
        Assert.Equal("odd level", e.Message);
    }

    [Fact]
    public void Crlf_and_lf_line_endings_both_work_and_blank_lines_are_tolerated()
    {
        var text = string.Join("\r\n",
            Line("08:00:00.012", "INF", "one"),
            string.Empty,
            Line("08:00:00.013", "INF", "two"),
            "continuation of two",
            string.Empty,
            string.Empty,
            Line("08:00:00.014", "INF", "three")) + "\r\n";

        var result = Parse(text);

        Assert.Equal(3, result.Events.Length);
        Assert.Equal("one", result.Events[0].Message);
        Assert.Equal("two\ncontinuation of two", result.Events[1].Message);
        Assert.Equal("three", result.Events[2].Message);
    }

    [Fact]
    public void File_without_a_trailing_newline_parses_the_last_event()
    {
        var text = Line("08:00:00.012", "INF", "one") + "\n" + Line("08:00:00.013", "ERR", "last");

        var result = Parse(text);

        Assert.Equal("last", result.Events[^1].Message);
    }

    [Fact]
    public void Raw_text_round_trips_through_offset_and_length()
    {
        var text = Join(
            Line("08:00:05.301", "ERR", "Payment failed"),
            "System.InvalidOperationException: boom",
            "   at A.B()",
            Line("08:00:05.601", "INF", "next"));
        var result = Parse(text);
        var doc = TestDocuments.FromText(text);

        Assert.Equal(
            Line("08:00:05.301", "ERR", "Payment failed") + "\nSystem.InvalidOperationException: boom\n   at A.B()",
            doc.GetRaw(result.Events[0]));
        Assert.Equal(Line("08:00:05.601", "INF", "next"), doc.GetRaw(result.Events[1]));
    }

    [Fact]
    public void Custom_template_is_honoured()
    {
        var template = OutputTemplate.Parse("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        var result = Parse("[08:00:00 INF] hello\n[08:00:01 ERR] bad\nSystem.Exception: x\n", template);

        Assert.Equal(2, result.Events.Length);
        Assert.Equal("bad", result.Events[1].Message);
        Assert.Equal("System.Exception: x", result.Events[1].Exception);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var text = string.Concat(Enumerable.Repeat(Line("08:00:00.012", "INF", "x") + "\n", 100_000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new PlainTextLogParser().Parse(text, null, null, cts.Token));
    }

    [Fact]
    public void Progress_reaches_one()
    {
        var text = string.Concat(Enumerable.Repeat(Line("08:00:00.012", "INF", "x") + "\n", 50_000));
        var reports = new List<double>();

        new PlainTextLogParser().Parse(text, null, new SynchronousProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public void Sample_file_parses_as_documented()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "serilog-text", "log20261113.txt");
        var text = File.ReadAllText(path);

        var result = Parse(text);

        Assert.Equal(14, result.Events.Length);
        Assert.Equal(0, result.UnparsedCount);
        var counts = result.Events.GroupBy(e => e.Level).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(7, counts[LogLevel.Information]);
        Assert.Equal(2, counts[LogLevel.Debug]);
        Assert.Equal(1, counts[LogLevel.Warning]);
        Assert.Equal(2, counts[LogLevel.Error]);
        Assert.Equal(1, counts[LogLevel.Verbose]);
        Assert.Equal(1, counts[LogLevel.Fatal]);

        var payment = result.Events.Single(e => e.Message.StartsWith("Payment failed"));
        Assert.Equal(LogLevel.Error, payment.Level);
        Assert.StartsWith("System.InvalidOperationException", payment.Exception);

        var multi = result.Events.Single(e => e.Message.StartsWith("Message with an embedded newline"));
        Assert.Equal(3, multi.Message.Split('\n').Length);
        Assert.Null(multi.Exception);

        var inner = result.Events.Single(e => e.Message.StartsWith("Failed to deserialize"));
        Assert.Contains("---> System.FormatException", inner.Exception);
        Assert.Contains("End of inner exception stack trace", inner.Exception);

        var fatal = result.Events.Single(e => e.Level == LogLevel.Fatal);
        Assert.StartsWith("System.OutOfMemoryException", fatal.Exception);
    }

    [Fact]
    public void Round_trips_a_large_generated_file_quickly()
    {
        // ~250 k events ≈ the largest expected file (30 MB, spec §1.4); generous bound, the real target is NFR-2 (< 1 s).
        var events = TestDocuments.Synthetic(250_000);
        var sb = new StringBuilder(events.Length * 120);
        foreach (var e in events)
        {
            var level = e.Level switch
            {
                LogLevel.Verbose => "VRB", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "FTL",
            };
            sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [").Append(level).Append("] ").Append(e.Message).Append("\r\n");
            if (e.Exception is not null)
            {
                sb.Append(e.Exception).Append("\r\n");
            }
        }

        var text = sb.ToString();
        var clock = Stopwatch.StartNew();
        var result = new PlainTextLogParser().Parse(text);
        clock.Stop();

        Assert.Equal(events.Length, result.Events.Length);
        Assert.Equal(0, result.UnparsedCount);
        Assert.Equal(events.Count(e => e.Exception is not null), result.Events.Count(e => e.Exception is not null));
        Assert.Equal(events.Select(e => e.Level), result.Events.Select(e => e.Level));
        Assert.Equal(events[12345].Message, result.Events[12345].Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"parsing took {clock.Elapsed.TotalSeconds:F1}s for {text.Length / 1_000_000.0:F0} MB");
        Console.WriteLine($"PARSE {text.Length / 1_000_000.0:F1} MB / {events.Length} events in {clock.ElapsedMilliseconds} ms");
    }

    private sealed class SynchronousProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
