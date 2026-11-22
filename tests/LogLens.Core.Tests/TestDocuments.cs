using LogLens.Core.Formats;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

/// <summary>Builders for test input.</summary>
internal static class TestDocuments
{
    /// <summary>A document parsed from <paramref name="text"/> in one piece, the way the loader builds one.</summary>
    public static LogDocument FromText(string text, string? path = "x.txt")
    {
        var document = new LogDocument(path, LogFormats.SerilogText);
        var parser = LogFormats.SerilogText.CreateParser(path);
        document.Text.Append(text);
        document.Apply(parser.Append(text));
        document.Apply(parser.Complete());
        return document;
    }

    /// <summary>Deterministic events with a realistic level mix; about one in fifty carries an exception.</summary>
    public static LogEvent[] Synthetic(int count, int seed = 42)
    {
        var random = new Random(seed);
        var start = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.FromMinutes(330));
        var events = new LogEvent[count];
        var time = start;
        for (var i = 0; i < count; i++)
        {
            time = time.AddMilliseconds(random.Next(1, 400));
            var roll = random.Next(1000);
            var level = roll switch
            {
                < 50 => LogLevel.Verbose,
                < 250 => LogLevel.Debug,
                < 900 => LogLevel.Information,
                < 960 => LogLevel.Warning,
                < 998 => LogLevel.Error,
                _ => LogLevel.Fatal,
            };
            var message = $"Order {random.Next(100_000)} handled for customer {random.Next(5_000)} in {random.Next(2_000)} ms";
            var exception = level >= LogLevel.Error && random.Next(2) == 0
                ? $"System.InvalidOperationException: Operation {i} failed\n   at Shop.Orders.Handler.Handle(Order order) in C:\\src\\Handler.cs:line {random.Next(500)}"
                : null;
            events[i] = new LogEvent(i, null, 0, 0, time, level, message, exception);
        }

        return events;
    }
}
