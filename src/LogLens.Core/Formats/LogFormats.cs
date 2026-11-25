using LogLens.Core.Formats.Json;
using LogLens.Core.Formats.Log4Net;
using LogLens.Core.Formats.NLog;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Formats.Text;

namespace LogLens.Core.Formats;

/// <summary>
/// The built-in formats (SPEC §2): Serilog's plain text with the default template (F1), compact JSON / CLEF (F2), classic JSON (F3), and the
/// text written by NLog's default layout and log4net's usual conversion pattern (F5). Custom formats (FR-F2) join them in a
/// <see cref="FormatCatalog"/>.
/// </summary>
public static class LogFormats
{
    public static ILogFormat SerilogText { get; } = new SerilogTextFormat();

    public static ILogFormat Clef { get; } = new ClefFormat();

    public static ILogFormat SerilogJson { get; } = new SerilogJsonFormat();

    /// <summary>NLog's file target with its default layout, <see cref="NLogLayout.DefaultText"/>.</summary>
    public static ILogFormat NLog { get; } = new TextLogFormat("nlog", "NLog text", NLogLayout.Default);

    /// <summary>log4net's <c>PatternLayout</c> with the usual conversion pattern, <see cref="Log4NetPattern.DefaultText"/>.</summary>
    public static ILogFormat Log4Net { get; } = new TextLogFormat("log4net", "log4net text", Log4NetPattern.Default);

    /// <summary>The built-in formats, in the order detection prefers them when several recognise the same lines.</summary>
    public static IReadOnlyList<ILogFormat> All { get; } = [Clef, SerilogJson, NLog, Log4Net, SerilogText];

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
