using LogLens.Core.Formats.Json;
using LogLens.Core.Formats.SerilogText;

namespace LogLens.Core.Formats;

/// <summary>
/// The built-in formats (SPEC §2): Serilog's plain text with the default template (F1), compact JSON / CLEF (F2) and classic JSON (F3).
/// Custom output templates (FR-F2) join them in a <see cref="FormatCatalog"/>.
/// </summary>
public static class LogFormats
{
    public static ILogFormat SerilogText { get; } = new SerilogTextFormat();

    public static ILogFormat Clef { get; } = new ClefFormat();

    public static ILogFormat SerilogJson { get; } = new SerilogJsonFormat();

    /// <summary>The built-in formats, in the order detection prefers them when several recognise the same lines.</summary>
    public static IReadOnlyList<ILogFormat> All { get; } = [Clef, SerilogJson, SerilogText];

    /// <summary>The format used when nothing else is known about a file.</summary>
    public static ILogFormat Default => SerilogText;

    /// <summary>Detects each file's format among the built-in formats (FR-F1).</summary>
    /// <remarks>Created on first use: <see cref="FormatCatalog"/>'s own static set-up reads the formats above, so it can't be built here.</remarks>
    public static ILogFormat Detect => field ??= new AutoDetectFormat(FormatCatalog.BuiltIn);

    /// <summary>
    /// The format of a view over several files (a merged view, FR-O4): the files' format when they share one, otherwise a format that names
    /// them all and offers every field any of them fills.
    /// </summary>
    public static ILogFormat Combine(IEnumerable<ILogFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var distinct = formats.Distinct().ToList();
        return distinct.Count switch
        {
            0 => Default,
            1 => distinct[0],
            _ => new CombinedFormat(distinct),
        };
    }

    /// <summary>Several formats shown as one (a merged view of files in different formats). It describes events; it can't parse.</summary>
    private sealed class CombinedFormat(IReadOnlyList<ILogFormat> parts) : ILogFormat
    {
        public string Id => string.Join('+', parts.Select(p => p.Id));

        public string DisplayName => string.Join(" + ", parts.Select(p => p.DisplayName));

        public LogFields Fields => parts.Aggregate(LogFields.None, (fields, p) => fields | p.Fields);

        public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
            => throw new NotSupportedException("A combined format describes a merged view; each file is parsed with its own format.");

        public bool Recognises(string line) => false;
    }
}
