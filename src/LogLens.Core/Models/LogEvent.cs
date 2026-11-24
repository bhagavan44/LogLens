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
/// <param name="Properties">Structured properties (null when the format has none, e.g. the default plain-text template).</param>
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
    /// <summary>The property that names the logging class, as Serilog writes it.</summary>
    public const string SourceContextProperty = "SourceContext";

    /// <summary>True for text that did not parse as an event (FR-F5): shown with its raw text instead of being dropped.</summary>
    public bool IsUnparsed => Level == LogLevel.Unparsed;

    /// <summary>The <c>SourceContext</c> property (the logging class) as text, when the event has one.</summary>
    public string? SourceContext => Properties is { } p && p.TryGetValue(SourceContextProperty, out var value) && !value.IsNull ? value.ToString() : null;

    /// <summary>The property called <paramref name="name"/>; null when the event has no such property.</summary>
    public LogValue? Property(string name) => Properties is { } p && p.TryGetValue(name, out var value) ? value : null;

    /// <summary>Events are equal when every field is, the properties compared by content.</summary>
    public bool Equals(LogEvent? other)
        => other is not null
           && Id == other.Id
           && SourceFile == other.SourceFile
           && Offset == other.Offset
           && Length == other.Length
           && Timestamp.Equals(other.Timestamp)
           && Level == other.Level
           && Message == other.Message
           && Exception == other.Exception
           && MessageTemplate == other.MessageTemplate
           && LogValue.PropertiesEqual(Properties, other.Properties);

    public override int GetHashCode() => HashCode.Combine(Id, SourceFile, Offset, Length, Timestamp, Level, Message);
}
