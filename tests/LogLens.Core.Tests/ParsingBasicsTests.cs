using System.Globalization;
using System.Text.RegularExpressions;
using LogLens.Core.Formats;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class LogLevelParserTests
{
    [Theory]
    [InlineData("VRB", LogLevel.Verbose)]
    [InlineData("DBG", LogLevel.Debug)]
    [InlineData("INF", LogLevel.Information)]
    [InlineData("WRN", LogLevel.Warning)]
    [InlineData("ERR", LogLevel.Error)]
    [InlineData("FTL", LogLevel.Fatal)]
    [InlineData("inf", LogLevel.Information)]
    [InlineData("V", LogLevel.Verbose)]
    [InlineData("d", LogLevel.Debug)]
    [InlineData("I", LogLevel.Information)]
    [InlineData("W", LogLevel.Warning)]
    [InlineData("E", LogLevel.Error)]
    [InlineData("F", LogLevel.Fatal)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("fatal", LogLevel.Fatal)]
    [InlineData("  ERR ", LogLevel.Error)]
    public void Recognises_serilog_level_text(string text, LogLevel expected)
        => Assert.Equal(expected, LogLevelParser.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("XYZ")]
    [InlineData("Q")]
    [InlineData("Noise")]
    public void Unknown_text_maps_to_Unknown(string text)
        => Assert.Equal(LogLevel.Unknown, LogLevelParser.Parse(text));
}

public class TimestampFormatTests
{
    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff zzz", "2026-11-13 08:00:00.012 +05:30")]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-11-13 08:00:00")]
    [InlineData("HH:mm:ss.fff", "08:00:00.012")]
    [InlineData("yyyy-MM-ddTHH:mm:ss.fffK", "2026-11-13T08:00:00.012Z")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.fffzzz", "2026-11-13T08:00:00.012+05:30")]
    [InlineData("dd/MM/yyyy HH:mm", "13/11/2026 08:00")]
    [InlineData("o", "2026-11-13T08:00:00.0120000+05:30")]
    public void Regex_matches_text_written_with_the_format(string format, string sample)
        => Assert.Matches("^" + TimestampFormat.ToRegex(format) + "$", sample);

    [Fact]
    public void Regex_rejects_text_in_another_shape()
        => Assert.DoesNotMatch("^" + TimestampFormat.ToRegex("yyyy-MM-dd HH:mm:ss.fff zzz") + "$", "13/11/2026 08:00:00");

    [Fact]
    public void Unsupported_specifiers_are_reported()
        => Assert.Throws<FormatException>(() => TimestampFormat.ToRegex("gg yyyy"));

    [Fact]
    public void Default_fast_path_reads_value_and_offset()
    {
        var parse = TimestampFormat.CreateParser(TimestampFormat.Default);

        Assert.True(parse("2026-11-13 08:00:00.012 +05:30", out var value));

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 0, 0, 12, TimeSpan.FromMinutes(330)), value);
        Assert.Equal(TimeSpan.FromMinutes(330), value.Offset);
    }

    [Fact]
    public void Default_fast_path_handles_negative_offsets()
    {
        var parse = TimestampFormat.CreateParser(TimestampFormat.Default);

        Assert.True(parse("2026-01-02 03:04:05.678 -08:00", out var value));

        Assert.Equal(TimeSpan.FromHours(-8), value.Offset);
        Assert.Equal(678, value.Millisecond);
    }

    [Theory]
    [InlineData("2026-13-13 08:00:00.012 +05:30")] // month 13
    [InlineData("2026-02-31 08:00:00.012 +05:30")] // 31 February
    [InlineData("2026-11-13 25:00:00.012 +05:30")] // hour 25
    [InlineData("2026-11-13 08:00:00.012 *05:30")] // bad sign
    [InlineData("2026-11-13 08:00:00.012 +05-30")] // bad separator
    [InlineData("2026-11-13 08:00:00.012")]         // no offset
    [InlineData("not a timestamp at all, really!!")]
    public void Default_fast_path_rejects_bad_input(string text)
        => Assert.False(TimestampFormat.CreateParser(TimestampFormat.Default)(text, out _));

    [Fact]
    public void Fast_path_agrees_with_TryParseExact()
    {
        var fast = TimestampFormat.CreateParser(TimestampFormat.Default);
        var rng = new Random(1);
        for (var i = 0; i < 500; i++)
        {
            var t = new DateTimeOffset(2000 + rng.Next(40), 1 + rng.Next(12), 1 + rng.Next(28), rng.Next(24), rng.Next(60), rng.Next(60), rng.Next(1000), TimeSpan.FromMinutes((rng.Next(-48, 57)) * 15));
            var text = t.ToString(TimestampFormat.Default, CultureInfo.InvariantCulture);

            Assert.True(fast(text, out var viaFast));
            Assert.True(DateTimeOffset.TryParseExact(text, TimestampFormat.Default, CultureInfo.InvariantCulture, DateTimeStyles.None, out var viaExact));
            Assert.Equal(viaExact, viaFast);
            Assert.Equal(viaExact.Offset, viaFast.Offset);
        }
    }

    [Fact]
    public void Generic_parser_assumes_local_time_when_no_offset()
    {
        var parse = TimestampFormat.CreateParser("yyyy-MM-dd HH:mm:ss");

        Assert.True(parse("2026-11-13 08:00:00", out var value));

        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 13, 8, 0, 0)), value.Offset);
    }
}

public class OutputTemplateTests
{
    [Fact]
    public void Default_template_matches_a_default_line_and_captures_fields()
    {
        var match = OutputTemplate.Default.StartOfEvent.Match("2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed for order 67204");

        Assert.True(match.Success);
        Assert.Equal("2026-11-13 08:00:05.301 +05:30", match.Groups["ts"].Value);
        Assert.Equal("ERR", match.Groups["lvl"].Value);
        Assert.Equal("Payment failed for order 67204", match.Groups["msg"].Value);
        Assert.True(OutputTemplate.Default.StartsWithDigit);
        Assert.Equal(TimestampFormat.Default, OutputTemplate.Default.TimestampFormat);
    }

    [Theory]
    [InlineData("   at Shop.Payments.PaymentService.Charge(Order order)")]
    [InlineData("System.InvalidOperationException: boom")]
    [InlineData("")]
    [InlineData("2026-11-13 08:00:05.301 +05:30 INF missing brackets")]
    public void Default_template_ignores_non_event_lines(string line)
        => Assert.DoesNotMatch(OutputTemplate.Default.StartOfEvent, line);

    [Fact]
    public void Custom_template_with_source_context_captures_it()
    {
        var template = OutputTemplate.Parse("[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        var match = template.StartOfEvent.Match("[08:00:05 WRN] Shop.Payments: Retry 2/3");

        Assert.True(match.Success);
        Assert.True(template.HasSourceContext);
        Assert.Equal([LogEvent.SourceContextProperty], template.PropertyNames);
        Assert.Equal("Shop.Payments", match.Groups["p0"].Value);
        Assert.Equal("WRN", match.Groups["lvl"].Value);
        Assert.Equal("Retry 2/3", match.Groups["msg"].Value);
    }

    [Fact]
    public void Level_width_is_respected()
    {
        var one = OutputTemplate.Parse("{Timestamp:HH:mm:ss} {Level:u1} {Message}");

        Assert.Matches(one.StartOfEvent, "08:00:05 E something");
        Assert.DoesNotMatch(one.StartOfEvent, "08:00:05 ERR something");
    }

    [Fact]
    public void Escaped_braces_are_literal()
    {
        var template = OutputTemplate.Parse("{{{Timestamp:HH:mm}}} {Level:u3} {Message}");

        Assert.Matches(template.StartOfEvent, "{08:00} INF hello");
    }

    [Theory]
    [InlineData("{Level:u3} {Message}")]                         // no timestamp
    [InlineData("{Timestamp:HH:mm} {Message}")]                  // no level
    [InlineData("{Timestamp:HH:mm} {Level:u3}")]                 // no message
    [InlineData("{Timestamp:HH:mm} {Level:u3} {Message} {Properties}")] // unsupported token
    [InlineData("{Timestamp:HH:mm} {Level:u3} {Message} {A-B}")]   // not a property name
    [InlineData("{Timestamp:HH:mm} {Level:u3} {A} {Message} {A}")] // a property twice
    [InlineData("{Timestamp:HH:mm {Level:u3} {Message}")]        // unterminated
    [InlineData("{Timestamp:HH:mm} {Level:u3} {Message} }")]     // stray }
    public void Invalid_templates_are_rejected(string template)
        => Assert.Throws<FormatException>(() => OutputTemplate.Parse(template));

    [Fact]
    public void Template_that_does_not_start_with_a_number_cannot_use_the_digit_shortcut()
    {
        var template = OutputTemplate.Parse("[{Level:u3}] {Timestamp:HH:mm:ss} {Message}");

        Assert.False(template.StartsWithDigit);
        Assert.Matches(template.StartOfEvent, "[INF] 08:00:05 hello");
    }
}
