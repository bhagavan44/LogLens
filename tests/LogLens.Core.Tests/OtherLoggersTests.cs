using LogLens.Core.Formats;
using LogLens.Core.Formats.Log4Net;
using LogLens.Core.Formats.NLog;
using LogLens.Core.Formats.Text;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

/// <summary>Shared helpers for the NLog and log4net tests.</summary>
internal static class OtherLoggers
{
    public static string Sample(string folder, string file)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", folder, file));

    public static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>Parses <paramref name="text"/> in one go with <paramref name="pattern"/>.</summary>
    public static LogEvent[] Parse(LinePattern pattern, string text) => new PlainTextLogParser(pattern).Parse(text.ReplaceLineEndings("\n"), "f.log").Events;

    /// <summary>The single event read from <paramref name="line"/>.</summary>
    public static LogEvent One(LinePattern pattern, string line)
    {
        var e = Assert.Single(Parse(pattern, line));
        Assert.False(e.IsUnparsed, $"'{line}' didn't match");
        return e;
    }

    public static DateTimeOffset Local(int hour, int minute, int second, int millisecond = 0, int microsecond = 0)
    {
        var dateTime = new DateTime(2026, 11, 13, hour, minute, second, millisecond, microsecond);
        return new DateTimeOffset(dateTime, TimeZoneInfo.Local.GetUtcOffset(dateTime));
    }
}

public class NLogLayoutTests
{
    [Fact]
    public void Default_layout_reads_time_level_logger_and_message()
    {
        var e = OtherLoggers.One(NLogLayout.Default, "2026-11-13 08:00:04.8884|WARN|Shop.Payments.PaymentService|Retry 2/3 after 664 ms");

        Assert.Equal(OtherLoggers.Local(8, 0, 4, 888, 400), e.Timestamp);
        Assert.Equal(LogLevel.Warning, e.Level);
        Assert.Equal("Retry 2/3 after 664 ms", e.Message);
        Assert.Equal("Shop.Payments.PaymentService", e.SourceContext);
        Assert.True(NLogLayout.Default.StartsWithDigit);
    }

    [Fact]
    public void A_pipe_in_the_message_stays_in_the_message()
        => Assert.Equal("a | b|c", OtherLoggers.One(NLogLayout.Default, "2026-11-13 08:00:00.0000|INFO|Shop|a | b|c").Message);

    [Fact]
    public void Exception_lines_and_extra_message_lines_attach_to_the_event()
    {
        var events = OtherLoggers.Parse(NLogLayout.Default, """
            2026-11-13 08:00:05.3017|ERROR|Shop.Payments|Payment failed
            more about it
            System.InvalidOperationException: Sequence contains no matching element
               at System.Linq.ThrowHelper.ThrowNoMatchException()
            2026-11-13 08:00:06.0000|INFO|Shop|next
            """);

        Assert.Equal(2, events.Length);
        Assert.Equal("Payment failed\nmore about it", events[0].Message);
        Assert.StartsWith("System.InvalidOperationException", events[0].Exception);
        Assert.EndsWith("ThrowNoMatchException()", events[0].Exception);
    }

    [Fact]
    public void An_exception_written_on_the_event_line_becomes_the_exception()
    {
        var pattern = NLogLayout.Parse("${longdate} ${level} ${message} ${exception:format=tostring}");
        var events = OtherLoggers.Parse(pattern, """
            2026-11-13 08:00:05.3017 Error Payment failed System.InvalidOperationException: Sequence contains no matching element
               at System.Linq.ThrowHelper.ThrowNoMatchException()
            2026-11-13 08:00:06.0000 Info Caught TimeoutException while retrying
            """);

        Assert.Equal("Payment failed", events[0].Message);
        Assert.Equal("System.InvalidOperationException: Sequence contains no matching element\n   at System.Linq.ThrowHelper.ThrowNoMatchException()", events[0].Exception);
        Assert.Equal("Caught TimeoutException while retrying", events[1].Message); // a mention is not an exception; the trimmed separator is fine
        Assert.Null(events[1].Exception);
    }

    [Fact]
    public void Date_renderers_with_formats_universal_time_and_split_date_and_time()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 11, 13, 8, 0, 4, 888, TimeSpan.Zero),
            OtherLoggers.One(NLogLayout.Parse("${date:format=yyyy-MM-ddTHH\\:mm\\:ss.fff:universalTime=true} ${message}"), "2026-11-13T08:00:04.888 x").Timestamp);
        Assert.Equal(
            OtherLoggers.Local(8, 0, 4, 888),
            OtherLoggers.One(NLogLayout.Parse("${date} ${message}"), "2026/11/13 08:00:04.888 x").Timestamp);
        Assert.Equal(
            OtherLoggers.Local(8, 0, 4, 888, 100),
            OtherLoggers.One(NLogLayout.Parse("${shortdate} ${time} [${level}] ${message}"), "2026-11-13 08:00:04.8881 [Info] x").Timestamp);
    }

    [Fact]
    public void Other_renderers_become_properties()
    {
        var pattern = NLogLayout.Parse(
            "${longdate} [${threadid}] ${level:uppercase=true:padding=-5} ${logger:shortName=true} ${event-properties:item=RequestId} ${mdlc:Tenant} ${machinename}: ${message}");
        var e = OtherLoggers.One(pattern, "2026-11-13 08:00:00.0000 [12] INFO  Orders 0HMV-1 acme WEB01: Order 42 shipped");

        Assert.Equal(["ThreadId", "SourceContext", "RequestId", "Tenant", "MachineName"], pattern.PropertyNames);
        Assert.Equal(new LogValue(12L), e.Property("ThreadId"));
        Assert.Equal("Orders", e.SourceContext);
        Assert.Equal("0HMV-1", e.Property("RequestId")?.ToString());
        Assert.Equal("acme", e.Property("Tenant")?.ToString());
        Assert.Equal("WEB01", e.Property("MachineName")?.ToString());
        Assert.Equal(LogLevel.Information, e.Level);
        Assert.Equal("Order 42 shipped", e.Message);
        Assert.Equal(LogFields.Exception | LogFields.Properties | LogFields.SourceContext, new TextLogFormat("n", "N", pattern).Fields);
    }

    [Fact]
    public void Wrappers_read_their_inner_layout_and_conditional_ones_are_optional()
    {
        var pattern = NLogLayout.Parse("${longdate}|${uppercase:${level}}|${pad:padding=-6:inner=${logger}}|${message}${onexception:inner= => ${exception}}");

        var plain = OtherLoggers.One(pattern, "2026-11-13 08:00:00.0000|INFO|Shop  |hello");
        var failed = OtherLoggers.One(pattern, "2026-11-13 08:00:00.0000|ERROR|Shop  |boom => System.Exception: bad");

        Assert.Equal(("hello", "Shop", LogLevel.Information), (plain.Message, plain.SourceContext, plain.Level));
        Assert.Null(plain.Exception);
        Assert.Equal("boom", failed.Message);
        Assert.Equal("System.Exception: bad", failed.Exception);
    }

    [Fact]
    public void A_new_line_ends_the_first_line_also_inside_onexception()
    {
        var pattern = NLogLayout.Parse("${longdate} ${level} ${message}${onexception:${newline}${exception:format=tostring}} ${logger}");
        var events = OtherLoggers.Parse(pattern, "2026-11-13 08:00:00.0000 Error boom\nSystem.Exception: bad\n");

        Assert.Equal("boom", events[0].Message);
        Assert.Equal("System.Exception: bad", events[0].Exception);
    }

    [Fact]
    public void Level_without_a_level_renderer_is_unknown_and_first_character_levels_read()
    {
        Assert.Equal(LogLevel.Unknown, OtherLoggers.One(NLogLayout.Parse("${longdate} ${message}"), "2026-11-13 08:00:00.0000 hello").Level);
        Assert.Equal(LogLevel.Verbose, OtherLoggers.One(NLogLayout.Parse("${longdate} ${level:format=FirstCharacter} ${message}"), "2026-11-13 08:00:00.0000 T hello").Level);
    }

    [Theory]
    [InlineData("${level} ${message}", "date or time")]
    [InlineData("${longdate} ${level}", "message")]
    [InlineData("${longdate} ${message} ${message}", "twice")]
    [InlineData("${longdate} ${message", "closing")]
    [InlineData("${longdate} ${} ${message}", "name")]
    [InlineData("${date:format=gg yyyy} ${message}", "gg yyyy")]
    public void Malformed_layouts_say_what_is_wrong(string layout, string expected)
        => Assert.Contains(expected, Assert.Throws<FormatException>(() => NLogLayout.Parse(layout)).Message);
}

public class Log4NetPatternTests
{
    [Fact]
    public void Default_pattern_reads_time_thread_level_logger_and_message()
    {
        var e = OtherLoggers.One(Log4NetPattern.Default, "2026-11-13 08:00:04,888 [12] WARN  Shop.Payments.PaymentService - Retry 2/3 after 664 ms");

        Assert.Equal(OtherLoggers.Local(8, 0, 4, 888), e.Timestamp);
        Assert.Equal(LogLevel.Warning, e.Level);
        Assert.Equal("Retry 2/3 after 664 ms", e.Message);
        Assert.Equal("Shop.Payments.PaymentService", e.SourceContext);
        Assert.Equal(new LogValue(12L), e.Property("Thread"));
        Assert.Equal(["Thread", "SourceContext"], Log4NetPattern.Default.PropertyNames);
    }

    [Fact]
    public void A_thread_name_with_spaces_and_a_dash_in_the_message_read_right()
    {
        var e = OtherLoggers.One(Log4NetPattern.Default, "2026-11-13 08:00:04,888 [Cache Worker] DEBUG Shop.Cache - hit - key 7");

        Assert.Equal("Cache Worker", e.Property("Thread")?.ToString());
        Assert.Equal("Shop.Cache", e.SourceContext);
        Assert.Equal("hit - key 7", e.Message);
    }

    [Fact]
    public void Exceptions_follow_on_the_next_lines()
    {
        var events = OtherLoggers.Parse(Log4NetPattern.Default, """
            2026-11-13 08:00:05,301 [12] ERROR Shop.Payments - Payment failed
            System.InvalidOperationException: Sequence contains no matching element
               at System.Linq.ThrowHelper.ThrowNoMatchException()
            2026-11-13 08:00:06,000 [1] INFO  Shop - next
            """);

        Assert.Equal(2, events.Length);
        Assert.Equal("Payment failed", events[0].Message);
        Assert.EndsWith("ThrowNoMatchException()", events[0].Exception);
    }

    [Theory]
    [InlineData("%d{ABSOLUTE} %m", "08:00:04,888 x")]
    [InlineData("%date{DATE} %m", "13 Nov 2026 08:00:04,888 x")]
    [InlineData("%date{ISO8601} %m", "2026-11-13 08:00:04,888 x")]
    [InlineData("%date{yyyy-MM-dd HH:mm:ss.fff} %m", "2026-11-13 08:00:04.888 x")]
    public void Date_conversions_read_their_named_and_custom_formats(string pattern, string line)
    {
        var e = OtherLoggers.One(Log4NetPattern.Parse(pattern), line);

        Assert.Equal(new TimeSpan(8, 0, 4) + TimeSpan.FromMilliseconds(888), e.Timestamp.TimeOfDay);
        Assert.Equal("x", e.Message);
    }

    [Fact]
    public void Utcdate_is_universal_time()
        => Assert.Equal(TimeSpan.Zero, OtherLoggers.One(Log4NetPattern.Parse("%utcdate %m"), "2026-11-13 08:00:04,888 x").Timestamp.Offset);

    [Fact]
    public void Short_names_padding_properties_and_percent_signs()
    {
        var pattern = Log4NetPattern.Parse("%d %5p %-10c{1} [%t] %X{RequestId} %property{user} %x 100%% %m%n%exception");
        var e = OtherLoggers.One(pattern, "2026-11-13 08:00:04,888  INFO Orders     [7] r-1 ann (null) 100% Order 42 shipped");

        Assert.Equal(["SourceContext", "Thread", "RequestId", "user", "NDC"], pattern.PropertyNames);
        Assert.Equal(LogLevel.Information, e.Level);
        Assert.Equal("Orders", e.SourceContext);
        Assert.Equal("r-1", e.Property("RequestId")?.ToString());
        Assert.Equal("ann", e.Property("user")?.ToString());
        Assert.Equal("Order 42 shipped", e.Message);
        Assert.False(pattern.HasException); // %exception after %newline is on the following lines
    }

    [Fact]
    public void Unknown_conversions_become_properties_named_after_them()
    {
        var pattern = Log4NetPattern.Parse("%date %level %aspnet-request{ALL_HTTP} %ndc %message");

        Assert.Equal(["AspnetRequest", "NDC"], pattern.PropertyNames);
    }

    [Theory]
    [InlineData("%level %message", "date or time")]
    [InlineData("%date %level", "message")]
    [InlineData("%date %level %message 50%", "%%")]
    [InlineData("%date{yyyy %message", "closing")]
    public void Malformed_patterns_say_what_is_wrong(string pattern, string expected)
        => Assert.Contains(expected, Assert.Throws<FormatException>(() => Log4NetPattern.Parse(pattern)).Message);
}

public class OtherLoggerFormatsTests
{
    [Fact]
    public void Built_in_formats_are_named_and_fill_logger_and_properties()
    {
        Assert.Equal(("nlog", "NLog text"), (LogFormats.NLog.Id, LogFormats.NLog.DisplayName));
        Assert.Equal(("log4net", "log4net text"), (LogFormats.Log4Net.Id, LogFormats.Log4Net.DisplayName));
        Assert.Equal(LogFields.Exception | LogFields.Properties | LogFields.SourceContext, LogFormats.NLog.Fields);
        Assert.Equal(LogFields.Exception | LogFields.Properties | LogFields.SourceContext, LogFormats.Log4Net.Fields);
    }

    [Theory]
    [InlineData("nlog", "nlog-20261113.log", "nlog")]
    [InlineData("log4net", "log4net-20261113.log", "log4net")]
    [InlineData("serilog-text", "log20261113.txt", "serilog-text")]
    public void Each_sample_is_detected_as_its_own_format(string folder, string file, string expected)
        => Assert.Equal(expected, FormatCatalog.BuiltIn.Detect(OtherLoggers.Lines(OtherLoggers.Sample(folder, file))).Id);

    [Theory]
    [InlineData("nlog", "nlog-20261113.log", 11, "TRACE")]
    [InlineData("log4net", "log4net-20261113.log", 11, "DEBUG")]
    public void Samples_parse_without_unparsed_lines(string folder, string file, int events, string _)
    {
        var format = FormatCatalog.BuiltIn.Find(folder)!;
        var parser = format.CreateParser(file);
        var text = OtherLoggers.Sample(folder, file);
        var all = parser.Append(text).Events.Concat(parser.Complete().Events).DistinctBy(e => e.Id).ToList();

        Assert.Equal(events, all.Count);
        Assert.DoesNotContain(all, e => e.IsUnparsed || e.Level == LogLevel.Unknown);
        Assert.Equal(2, all.Count(e => e.Exception is not null));
        Assert.All(all, e => Assert.NotNull(e.SourceContext));
    }

    [Fact]
    public void Feeding_an_nlog_file_in_chunks_gives_the_same_events_as_one_pass()
    {
        var text = OtherLoggers.Sample("nlog", "nlog-20261113.log").ReplaceLineEndings("\r\n");
        var expected = new PlainTextLogParser(NLogLayout.Default).Parse(text, "a.log").Events;

        var document = new LogDocument("a.log", LogFormats.NLog);
        var parser = LogFormats.NLog.CreateParser("a.log");
        for (var i = 0; i < text.Length; i += 7)
        {
            var piece = text.Substring(i, Math.Min(7, text.Length - i));
            document.Text.Append(piece);
            document.Apply(parser.Append(piece));
        }

        document.Apply(parser.Complete());

        Assert.Equal(expected, document.Events);
    }

    [Theory]
    [InlineData("Trace", LogLevel.Verbose)]
    [InlineData("TRACE", LogLevel.Verbose)]
    [InlineData("T", LogLevel.Verbose)]
    [InlineData("trce", LogLevel.Verbose)]
    [InlineData("dbug", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Information)]
    [InlineData("NOTICE", LogLevel.Information)]
    [InlineData("WARN", LogLevel.Warning)]
    [InlineData("fail", LogLevel.Error)]
    [InlineData("SEVERE", LogLevel.Error)]
    [InlineData("CRITICAL", LogLevel.Fatal)]
    [InlineData("crit", LogLevel.Fatal)]
    [InlineData("EMERGENCY", LogLevel.Fatal)]
    [InlineData("Informational", LogLevel.Unknown)]
    public void Level_names_of_other_loggers_map_to_levels(string text, LogLevel expected)
        => Assert.Equal(expected, LogLevelParser.Parse(text));
}
