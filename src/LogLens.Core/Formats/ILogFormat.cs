using LogLens.Core.Models;

namespace LogLens.Core.Formats;

/// <summary>The optional <see cref="LogEvent"/> fields a format fills, so the UI can hide features that would always be empty.</summary>
[Flags]
public enum LogFields
{
    None = 0,
    Exception = 1 << 0,
    MessageTemplate = 1 << 1,
    Properties = 1 << 2,
    SourceContext = 1 << 3,
}

/// <summary>
/// A log format (SPEC §2, §6.2): a named parser factory. Every format turns text into the common <see cref="LogEvent"/> model, so loading,
/// the grid, filters and export never special-case a format. Adding a format means adding an implementation of this interface, its tests
/// and a <c>samples/&lt;format&gt;/</c> folder, and registering it in <see cref="LogFormats"/> (and so in every <see cref="FormatCatalog"/>).
/// </summary>
public interface ILogFormat
{
    /// <summary>Stable identifier, e.g. <c>serilog-text</c>.</summary>
    string Id { get; }

    /// <summary>Name shown in the status bar, e.g. "Serilog text".</summary>
    string DisplayName { get; }

    /// <summary>Which optional fields this format fills.</summary>
    LogFields Fields { get; }

    /// <summary>
    /// Creates a parser for one stream of text (a file), whose events carry <paramref name="sourceFile"/>. A parser that continues a document
    /// — the next file of a rolling log (FR-T3) — starts at character offset <paramref name="startOffset"/> and event id <paramref name="firstId"/>.
    /// </summary>
    ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0);

    /// <summary>
    /// True when <paramref name="line"/> (one line, without its newline) starts an event of this format. Format detection (FR-F1) counts the
    /// lines each format recognises among a file's first lines.
    /// </summary>
    bool Recognises(string line);
}

/// <summary>
/// Parses text that arrives in pieces — the chunks of a file being read, later the lines appended to a live log — into the same events a
/// single pass over the whole text would produce. Not thread-safe; one instance per stream.
/// </summary>
/// <remarks>
/// Contract: only complete lines are interpreted (an unterminated last line is held back until its newline arrives, FR-T6). The last event of
/// a delta may be provisional — later text can add continuation lines to it — which shows up as a later delta that replaces it.
/// </remarks>
public interface ILogParser
{
    /// <summary>Events with a lower id are final; this id and above are provisional (the event still open, or only previewed).</summary>
    int CommittedId { get; }

    /// <summary>Absolute character offset just past all the text given so far.</summary>
    long EndOffset { get; }

    /// <summary>True when the text held back ends in the middle of a line (FR-T6).</summary>
    bool HasPartialLine { get; }

    /// <summary>Adds the next piece of the stream and returns what changed.</summary>
    ParseDelta Append(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// The events as they would be if the stream ended now — an unterminated last line included — <b>without</b> changing any state. Shows the
    /// last line of a file that has stopped growing without a final newline; the next <see cref="Append"/> supersedes it.
    /// </summary>
    ParseDelta Preview();

    /// <summary>Ends the stream: text held back (an unterminated last line, the open event) becomes final. Returns the closing delta.</summary>
    ParseDelta Complete();
}

/// <summary>
/// A change to a document's event list: <b>remove every event with an id ≥ <see cref="FirstId"/>, then append <see cref="Events"/></b>
/// (whose ids run on from <see cref="FirstId"/>). That one rule covers appending events and replacing a provisional last event.
/// </summary>
public sealed record ParseDelta(int FirstId, IReadOnlyList<LogEvent> Events);
