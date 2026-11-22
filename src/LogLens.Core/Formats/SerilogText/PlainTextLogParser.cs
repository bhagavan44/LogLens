using System.Text.RegularExpressions;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.SerilogText;

/// <summary>Result of parsing a whole file.</summary>
public sealed record ParseResult(LogEvent[] Events, int UnparsedCount);

/// <summary>
/// Parses plain-text Serilog files written with an <see cref="OutputTemplate"/> (v1: the default template).
/// A line matching the template starts a new event; following lines that don't match belong to it (FR-F3).
/// Non-blank text that precedes the first event is kept as <em>unparsed</em> events instead of being dropped (FR-F5);
/// later lines that don't match are continuation lines of the event before them.
/// </summary>
/// <remarks>
/// <see cref="Parse"/> handles a complete file. <see cref="SerilogTextParser"/> drives the same <see cref="ParseChunk"/> routine
/// for text that arrives in pieces (progressive loading and live tail), so both paths produce identical events.
/// </remarks>
public sealed class PlainTextLogParser
{
    private const int CheckpointLines = 1 << 14;

    // "System.InvalidOperationException: message", "ArgumentException", "Foo.Error:", "System.Exception: x" ...
    // A bare "Exception" / "Error" with no namespace or prefix is deliberately NOT a header ("Error: disk full" is a message).
    private static readonly Regex ExceptionHeader = new(
        @"^(?:(?:[A-Za-z_][\w`+<>]*\.)+(?:Exception|Error)|(?:[A-Za-z_][\w`+<>]*\.)*[A-Za-z_][\w`+<>]*(?:Exception|Error))(?::|\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private readonly OutputTemplate _template;

    public PlainTextLogParser(OutputTemplate? template = null)
    {
        _template = template ?? OutputTemplate.Default;
    }

    public ParseResult Parse(string text, string? sourceFile = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = ParseChunk(text, baseOffset: 0, firstId: 0, sourceFile, final: true, progress, cancellationToken);
        return new ParseResult(result.Events.ToArray(), result.Unparsed);
    }

    /// <summary>The outcome of <see cref="ParseChunk"/>.</summary>
    /// <param name="Events">Events found, with ids starting at the requested first id. When not final, the last one is <em>provisional</em>: later lines may still extend it.</param>
    /// <param name="CarryFrom">Index into the parsed text from which the caller must carry text over to the next call (start of the open event, or of the unfinished last line).</param>
    /// <param name="EndsOpen">True when the last event in <paramref name="Events"/> is provisional.</param>
    /// <param name="Unparsed">Number of unparsed lines among <paramref name="Events"/>.</param>
    internal readonly record struct ChunkResult(List<LogEvent> Events, int CarryFrom, bool EndsOpen, int Unparsed);

    /// <summary>
    /// Parses <paramref name="text"/>, which starts at absolute character offset <paramref name="baseOffset"/>. When <paramref name="final"/> is false
    /// an unterminated last line is left alone (it may still be mid-write) and the last event is reported as provisional (FR-T6).
    /// </summary>
    internal ChunkResult ParseChunk(
        string text,
        long baseOffset,
        int firstId,
        string? sourceFile,
        bool final,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var events = new List<LogEvent>(Math.Max(16, text.Length / 120));
        var unparsed = 0;
        var state = new EventBuilder();
        var lineNumber = 0;
        var pos = 0;
        var partialStart = -1;

        while (pos < text.Length)
        {
            var lineStart = pos;
            var newline = text.IndexOf('\n', pos);
            if (newline < 0 && !final)
            {
                partialStart = lineStart; // unfinished line: wait for its newline
                break;
            }

            var lineEndWithBreak = newline < 0 ? text.Length : newline + 1;
            var lineEnd = newline < 0 ? text.Length : newline;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            pos = lineEndWithBreak;

            if ((++lineNumber & (CheckpointLines - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)pos / text.Length);
            }

            if (TryStartEvent(text, lineStart, lineEnd, out var timestamp, out var level, out var firstLineMessage))
            {
                Flush();
                state.Begin(lineStart, lineEnd, timestamp, level, firstLineMessage);
                continue;
            }

            if (state.Active)
            {
                state.AddContinuation(text, lineStart, lineEnd);
                continue;
            }

            // Text before the first event: keep it visible, one entry per non-blank line.
            if (lineEnd > lineStart)
            {
                var raw = text.Substring(lineStart, lineEnd - lineStart);
                events.Add(new LogEvent(firstId + events.Count, sourceFile, baseOffset + lineStart, raw.Length, DateTimeOffset.MinValue, LogLevel.Unparsed, raw, null));
                unparsed++;
            }
        }

        int carryFrom;
        var endsOpen = false;
        if (final)
        {
            Flush();
            carryFrom = text.Length;
        }
        else if (state.Active)
        {
            carryFrom = state.Start; // the open event (and any unfinished line after it) is re-read with the next chunk
            Flush();
            endsOpen = true;
        }
        else
        {
            carryFrom = partialStart >= 0 ? partialStart : text.Length;
        }

        progress?.Report(1);
        return new ChunkResult(events, carryFrom, endsOpen, unparsed);

        void Flush()
        {
            if (state.Active)
            {
                events.Add(state.Build(text, firstId + events.Count, sourceFile, baseOffset));
                state.Reset();
            }
        }
    }

    private bool TryStartEvent(string text, int start, int end, out DateTimeOffset timestamp, out LogLevel level, out string message)
    {
        timestamp = default;
        level = LogLevel.Unknown;
        message = string.Empty;

        var length = end - start;
        if (length == 0 || (_template.StartsWithDigit && (uint)(text[start] - '0') > 9))
        {
            return false;
        }

        Match match;
        try
        {
            match = _template.StartOfEvent.Match(text, start, length);
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // NFR-10: a pathological line is a continuation line, never a crash
        }

        if (!match.Success)
        {
            return false;
        }

        var ts = match.Groups["ts"];
        if (!_template.ParseTimestamp(text.AsSpan(ts.Index, ts.Length), out timestamp))
        {
            return false;
        }

        var lvl = match.Groups["lvl"];
        level = LogLevelParser.Parse(text.AsSpan(lvl.Index, lvl.Length));
        var msg = match.Groups["msg"];
        message = text.Substring(msg.Index, msg.Length);
        return true;
    }

    /// <summary>True if <paramref name="line"/> begins an exception: a "Namespace.XyzException: ..." header or a "   at Foo.Bar(...)" frame.</summary>
    internal static bool IsExceptionStart(ReadOnlySpan<char> line)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (char.IsWhiteSpace(line[0]))
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("at ", StringComparison.Ordinal) && trimmed.Contains('(');
        }

        try
        {
            return ExceptionHeader.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private struct EventBuilder
    {
        private int _start;
        private int _end;
        private DateTimeOffset _timestamp;
        private LogLevel _level;
        private string _message;
        private int _messageContinuationStart;
        private int _exceptionStart;
        private bool _active;

        public readonly bool Active => _active;

        /// <summary>Index in the parsed text of the first character of the event being built.</summary>
        public readonly int Start => _start;

        public void Begin(int start, int lineEnd, DateTimeOffset timestamp, LogLevel level, string message)
        {
            _active = true;
            _start = start;
            _end = lineEnd;
            _timestamp = timestamp;
            _level = level;
            _message = message;
            _messageContinuationStart = -1;
            _exceptionStart = -1;
        }

        public void AddContinuation(string text, int lineStart, int lineEnd)
        {
            if (lineEnd == lineStart)
            {
                return; // blank line: kept inside the event only if more text follows (see _end)
            }

            _end = lineEnd;
            if (_exceptionStart >= 0)
            {
                return;
            }

            // The exception starts at the first continuation line that looks like one; text before it extends the message.
            if (IsExceptionStart(text.AsSpan(lineStart, lineEnd - lineStart)))
            {
                _exceptionStart = lineStart;
            }
            else if (_messageContinuationStart < 0)
            {
                _messageContinuationStart = lineStart;
            }
        }

        public readonly LogEvent Build(string text, int id, string? sourceFile, long baseOffset)
        {
            var message = _message;
            string? exception = null;

            if (_exceptionStart >= 0)
            {
                exception = text.Substring(_exceptionStart, _end - _exceptionStart);
                if (_messageContinuationStart >= 0)
                {
                    var messageEnd = TrimEnd(text, _messageContinuationStart, _exceptionStart);
                    message = string.Concat(_message, "\n", text.AsSpan(_messageContinuationStart, messageEnd - _messageContinuationStart));
                }
            }
            else if (_messageContinuationStart >= 0)
            {
                message = string.Concat(_message, "\n", text.AsSpan(_messageContinuationStart, _end - _messageContinuationStart));
            }

            return new LogEvent(id, sourceFile, baseOffset + _start, _end - _start, _timestamp, _level, message, exception);
        }

        public void Reset() => this = default;

        private static int TrimEnd(string text, int start, int end)
        {
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            return end;
        }
    }
}
