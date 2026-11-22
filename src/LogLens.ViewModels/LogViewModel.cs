using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>One open log: its document, the selected event and the text of the detail pane and status bar.</summary>
public sealed partial class LogViewModel : ObservableObject
{
    public LogViewModel(LogDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public LogDocument Document { get; }

    /// <summary>What the grid shows: every event, oldest first.</summary>
    public IReadOnlyList<LogEvent> Events => Document.Events;

    /// <summary>The active row of the grid; the detail pane follows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DetailHeader), nameof(MessageText), nameof(HasException), nameof(ExceptionText), nameof(RawText), nameof(MetadataText))]
    public partial LogEvent? SelectedEvent { get; set; }

    public bool HasSelection => SelectedEvent is not null;

    // ----- detail pane (FR-D1 … FR-D4) -----

    public string DetailHeader => SelectedEvent is { } e
        ? $"{LogText.LevelName(e.Level)}  ·  {LogText.FullTime(e)}"
        : "Select an event to see its details";

    /// <summary>FR-D1: the full message.</summary>
    public string MessageText => SelectedEvent?.Message ?? string.Empty;

    public bool HasException => SelectedEvent?.Exception is not null;

    /// <summary>FR-D2: the exception with its stack trace.</summary>
    public string ExceptionText => SelectedEvent switch
    {
        null => string.Empty,
        { Exception: { } text } => text,
        _ => "No exception was logged with this event.",
    };

    /// <summary>FR-D3: the original line(s) exactly as in the file.</summary>
    public string RawText => SelectedEvent is { } e ? Document.GetRaw(e) : string.Empty;

    /// <summary>FR-D4: file name, line and event number.</summary>
    public string MetadataText => SelectedEvent is { } e
        ? string.Create(CultureInfo.InvariantCulture, $"{Document.FileName}  ·  line {Document.LineNumberOf(e):N0}  ·  event {e.Id + 1:N0} of {Document.Events.Count:N0}")
        : string.Empty;

    // ----- status bar -----

    public string StatusText
    {
        get
        {
            var d = Document;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"{d.Events.Count:N0} {(d.Events.Count == 1 ? "event" : "events")}");
            Append(sb, d.CountOf(LogLevel.Fatal), "fatal", "fatal");
            Append(sb, d.CountOf(LogLevel.Error), "error", "errors");
            Append(sb, d.CountOf(LogLevel.Warning), "warning", "warnings");
            Append(sb, d.UnparsedCount, "unparsed line", "unparsed lines");
            sb.Append("  ·  ").Append(d.Format.DisplayName);
            sb.Append("  ·  ").Append(d.EncodingName);
            sb.Append("  ·  ").Append(FormatSize(d.FileSizeBytes));
            if (d.LoadTime > TimeSpan.Zero)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  ·  loaded in {d.LoadTime.TotalMilliseconds:N0} ms");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Text for the clipboard (FR-G4): rendered rows as tab-separated Time, Level, Message (exception included), or the original lines.
    /// </summary>
    public string BuildCopyText(IEnumerable<LogEvent> events, bool raw)
    {
        ArgumentNullException.ThrowIfNull(events);
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            if (sb.Length > 0)
            {
                sb.Append("\r\n");
            }

            if (raw)
            {
                sb.Append(Document.GetRaw(e));
                continue;
            }

            sb.Append(LogText.Time(e)).Append('\t').Append(LogText.LevelAbbreviation(e.Level)).Append('\t').Append(e.Message);
            if (e.Exception is not null)
            {
                sb.Append("\r\n").Append(e.Exception);
            }
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, int count, string singular, string plural)
    {
        if (count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  ·  {count:N0} {(count == 1 ? singular : plural)}");
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };
}
