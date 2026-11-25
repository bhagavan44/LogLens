using LogLens.Core.Formats.Text;

namespace LogLens.Core.Formats.SerilogText;

/// <summary>
/// Serilog's plain-text file output (SPEC §2 F1) written with an <see cref="OutputTemplate"/>: by default the file sink's
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}</c>, or a custom template from the settings
/// (FR-F2), whose property tokens become properties of the events.
/// </summary>
public sealed class SerilogTextFormat : ILogFormat
{
    public const string DefaultId = "serilog-text";

    /// <summary>Custom templates have ids made of this prefix and their name.</summary>
    public const string CustomIdPrefix = "template:";

    private static readonly char[] CrLf = ['\r', '\n'];

    private readonly PlainTextLogParser _probe;

    /// <param name="template">The output template; the default one when null.</param>
    /// <param name="name">A custom template's name, shown as the format's name; null for the built-in default.</param>
    public SerilogTextFormat(OutputTemplate? template = null, string? name = null)
    {
        Template = template ?? OutputTemplate.Default;
        _probe = new PlainTextLogParser(Template.Pattern);
        Id = name is null ? DefaultId : CustomIdPrefix + name;
        DisplayName = name ?? "Serilog text";
    }

    public OutputTemplate Template { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public LogFields Fields => Template.Pattern.Fields;

    public ILogParser CreateParser(string? sourceFile, long startOffset = 0, int firstId = 0)
        => new TextLogParser(Template.Pattern, sourceFile, startOffset, firstId);

    /// <summary>The line is the first line of an event written with this template.</summary>
    public bool Recognises(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _probe.IsEventStart(line.TrimEnd(CrLf));
    }
}
