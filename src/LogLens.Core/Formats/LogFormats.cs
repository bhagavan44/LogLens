using LogLens.Core.Formats.SerilogText;

namespace LogLens.Core.Formats;

/// <summary>
/// The formats LogLens can read. v1.0 has exactly one (Serilog's default plain-text template, SPEC §2 F1), so every file uses it; per-file
/// detection (FR-F1) arrives together with the second format.
/// </summary>
public static class LogFormats
{
    public static ILogFormat SerilogText { get; } = new SerilogTextFormat();

    public static IReadOnlyList<ILogFormat> All { get; } = [SerilogText];

    /// <summary>The format used when nothing else is known about a file.</summary>
    public static ILogFormat Default => SerilogText;
}
