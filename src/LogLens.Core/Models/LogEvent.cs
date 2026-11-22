namespace LogLens.Core.Models;

/// <summary>
/// One log event in the common model every format produces (SPEC §6.4). The grid, filters, statistics and export only ever see this
/// type, never a format-specific one.
/// </summary>
/// <param name="Id">Sequential index of the event within its document (0-based); also its position in <see cref="LogDocument.Events"/>.</param>
/// <param name="SourceFile">File the event was read from (null for events not read from a file).</param>
/// <param name="Offset">Character offset of the event's first character in the document's text (<see cref="LogDocument.Text"/>).</param>
/// <param name="Length">Length in characters of the raw event text, continuation lines included, without the trailing newline.</param>
/// <param name="Timestamp">When the event was logged; <see cref="DateTimeOffset.MinValue"/> for unparsed text.</param>
/// <param name="Level">Severity, or <see cref="LogLevel.Unknown"/> / <see cref="LogLevel.Unparsed"/>.</param>
/// <param name="Message">Rendered message. May span several lines; the exception, if any, is held separately.</param>
/// <param name="Exception">Exception text (type line and stack frames) when the event carries one.</param>
/// <param name="MessageTemplate">The message template, for structured formats only (null for plain text).</param>
/// <param name="Properties">Structured properties, for structured formats only (null for plain text).</param>
public sealed record LogEvent(
    int Id,
    string? SourceFile,
    long Offset,
    int Length,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Exception,
    string? MessageTemplate = null,
    IReadOnlyDictionary<string, LogValue>? Properties = null)
{
    /// <summary>True for text that did not parse as an event (FR-F5): shown with its raw text instead of being dropped.</summary>
    public bool IsUnparsed => Level == LogLevel.Unparsed;
}

/// <summary>The value of a structured property (structured formats arrive after v1.0, SPEC §8).</summary>
/// <param name="Value">A scalar (string, number, boolean, null) as read from the log.</param>
public sealed record LogValue(object? Value)
{
    public override string ToString() => Value?.ToString() ?? "null";
}
