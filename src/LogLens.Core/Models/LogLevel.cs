namespace LogLens.Core.Models;

/// <summary>
/// Severity of a <see cref="LogEvent"/>. <see cref="Verbose"/> … <see cref="Fatal"/> are ordered by severity, so they compare with
/// <c>&lt;</c> / <c>&gt;</c>. <see cref="Unknown"/> is level text no format recognises (kept verbatim in the raw text, FR-F4);
/// <see cref="Unparsed"/> marks text that did not parse as an event at all (FR-F5).
/// </summary>
public enum LogLevel : byte
{
    Unknown = 0,
    Verbose = 1,
    Debug = 2,
    Information = 3,
    Warning = 4,
    Error = 5,
    Fatal = 6,
    Unparsed = 7,
}
