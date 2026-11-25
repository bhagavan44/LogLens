using LogLens.Core.Formats.SerilogText;

namespace LogLens.Core.Formats;

/// <summary>A custom output template from the settings (FR-F2): a name the user gave it and the template text.</summary>
public sealed record CustomTemplate(string Name, string Template);

/// <summary>
/// The formats a file can be read with: the built-in ones (<see cref="LogFormats"/>) plus the user's custom output templates (FR-F2). Picks a
/// file's format from its first lines (FR-F1).
/// </summary>
public sealed class FormatCatalog
{
    /// <summary>How many non-empty lines detection looks at.</summary>
    public const int DetectionLines = 50;

    /// <summary>The built-in formats only.</summary>
    public static FormatCatalog BuiltIn { get; } = new([]);

    /// <param name="customTemplates">Custom templates; ones that don't compile, or whose name is taken, are left out.</param>
    public FormatCatalog(IEnumerable<CustomTemplate> customTemplates)
    {
        ArgumentNullException.ThrowIfNull(customTemplates);
        var custom = new List<ILogFormat>();
        foreach (var template in customTemplates)
        {
            if (string.IsNullOrWhiteSpace(template.Name) || custom.Any(f => f.DisplayName.Equals(template.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (TryCompile(template.Template, out var compiled, out _))
            {
                custom.Add(new SerilogTextFormat(compiled, template.Name.Trim()));
            }
        }

        // Detection prefers the stricter formats: JSON first, then the custom templates (a custom template usually also matches lines a
        // built-in text format would), then NLog and log4net, and Serilog's default template last.
        All = [LogFormats.Clef, LogFormats.SerilogJson, .. custom, LogFormats.NLog, LogFormats.Log4Net, LogFormats.SerilogText];
        Custom = custom;
    }

    /// <summary>Every format, in detection order.</summary>
    public IReadOnlyList<ILogFormat> All { get; }

    /// <summary>The formats made from custom templates.</summary>
    public IReadOnlyList<ILogFormat> Custom { get; }

    /// <summary>The format used when detection finds nothing.</summary>
    public ILogFormat Default => LogFormats.Default;

    /// <summary>The format with <paramref name="id"/>, or null.</summary>
    public ILogFormat? Find(string? id) => id is null ? null : All.FirstOrDefault(f => f.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>
    /// FR-F1: the format that recognises the most of the first <see cref="DetectionLines"/> non-empty lines; on a tie, the one listed first in
    /// <see cref="All"/>. <see cref="Default"/> when no format recognises any of them.
    /// </summary>
    public ILogFormat Detect(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sample = lines.Select(l => l.TrimEnd('\r', '\n')).Where(l => !string.IsNullOrWhiteSpace(l)).Take(DetectionLines).ToList();
        ILogFormat? best = null;
        var bestCount = 0;
        foreach (var format in All)
        {
            var count = 0;
            foreach (var line in sample)
            {
                if (format.Recognises(line))
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                best = format;
                bestCount = count;
            }
        }

        return best ?? Default;
    }

    /// <summary>
    /// What is wrong with a custom template being edited (FR-F2), or null when it is fine: a missing or duplicate name (names compare
    /// ignoring case, also with the built-in formats) or a template that doesn't compile.
    /// </summary>
    public static string? CheckCustomTemplate(string? name, string? template, IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(otherNames);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the template a name.";
        }

        var trimmed = name.Trim();
        if (otherNames.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            || LogFormats.All.Any(f => string.Equals(f.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"There is already a format called '{trimmed}'.";
        }

        return TryCompile(template, out _, out var error) ? null : error;
    }

    /// <summary>
    /// The "test a line" preview of the template editor: what <paramref name="template"/> reads from <paramref name="line"/> (time, level,
    /// message and properties), or why it doesn't.
    /// </summary>
    public static string TestLine(string? template, string? line)
    {
        if (!TryCompile(template, out var compiled, out var error))
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return "Paste the first line of an event to see what the template reads from it.";
        }

        var parser = new Text.TextLogParser(compiled!.Pattern);
        var events = parser.Append(line.TrimEnd('\r', '\n') + "\n").Events.Concat(parser.Complete().Events).ToList();
        var e = events.LastOrDefault();
        if (e is null || e.IsUnparsed)
        {
            return "The line doesn't match the template.";
        }

        var parts = new List<string>
        {
            "Time " + e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture),
            "level " + e.Level,
            $"message \"{e.Message}\"",
        };
        if (e.Properties is { } properties)
        {
            parts.AddRange(properties.Select(p => $"{p.Key} = {p.Value}"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Compiles a custom template, or says what is wrong with it.</summary>
    public static bool TryCompile(string? template, out OutputTemplate? compiled, out string? error)
    {
        compiled = null;
        error = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Enter an output template.";
            return false;
        }

        try
        {
            compiled = OutputTemplate.Parse(template);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message; // a timestamp format the regex builder could not turn into a pattern
            return false;
        }
    }
}
