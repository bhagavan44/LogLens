using System.Globalization;
using LogLens.Core.Export;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>Export (FR-E1) and copy as a Markdown table.</summary>
public sealed partial class LogViewModel
{
    /// <summary>How many events <see cref="ExportAsync"/> would write for the current filters (or the given selection).</summary>
    public int ExportableCount(IReadOnlyCollection<LogEvent>? selection = null) => selection?.Count ?? View.Count;

    /// <summary>
    /// Writes the events currently listed, or just <paramref name="selection"/>, to <paramref name="destination"/> in <paramref name="format"/>,
    /// in file order. The work runs on a worker thread; new events wait in the queue meanwhile so the text being read cannot change underneath
    /// it. The stream is left open. Returns how many events were written.
    /// </summary>
    public async Task<int> ExportAsync(ExportFormat format, Stream destination, IReadOnlyList<LogEvent>? selection = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // A file is written oldest first even when the list shows the newest event at the top.
        IReadOnlyList<LogEvent> events = selection is null ? View.Chronological.ToArray() : selection.OrderBy(e => e.Id).ToArray();
        var options = new ExportOptions { Title = Document.FileName, Description = Describe(events.Count, selection is not null) };
        var document = Document;

        BeginPass();
        try
        {
            await Task.Run(
                () =>
                {
                    using var writer = new StreamWriter(destination, LogExporter.EncodingFor(format), 64 * 1024, leaveOpen: true);
                    LogExporter.Write(format, events, document, writer, options, cancellationToken);
                },
                cancellationToken);
        }
        finally
        {
            EndPass();
        }

        return events.Count;
    }

    /// <summary>The events as a Markdown table, for pasting into a ticket.</summary>
    public async Task<string> BuildMarkdownAsync(IReadOnlyList<LogEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.OrderBy(e => e.Id).ToArray();
        var document = Document;

        BeginPass();
        try
        {
            return await Task.Run(
                () =>
                {
                    var writer = new StringWriter(CultureInfo.InvariantCulture);
                    LogExporter.Write(ExportFormat.Markdown, ordered, document, writer, cancellationToken: cancellationToken);
                    return writer.ToString();
                },
                cancellationToken);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>Tells the user an export finished (shown in the message bar).</summary>
    public void ReportExported(string path, int count)
        => NoticeRaised?.Invoke(
            this,
            new Notice(NoticeSeverity.Informational, string.Create(CultureInfo.InvariantCulture, $"Exported {count:N0} event{(count == 1 ? string.Empty : "s")}"), path));

    /// <summary>Tells the user an export could not be written.</summary>
    public void ReportExportFailed(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        NoticeRaised?.Invoke(this, new Notice(NoticeSeverity.Error, $"Could not export to {Path.GetFileName(path)}", exception.Message));
    }

    /// <summary>A suggested file name for an export: the log's name plus a marker when only part of it is written.</summary>
    public string SuggestedExportName(ExportFormat format, bool selectionOnly)
    {
        var baseName = Document.IsMerged ? "merged-log" : Path.GetFileNameWithoutExtension(Document.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "log";
        }

        var marker = selectionOnly ? "-selection" : View.IsFiltered ? "-filtered" : string.Empty;
        return baseName + marker + LogExporter.FileExtension(format);
    }

    private string Describe(int count, bool selection)
    {
        if (selection)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count:N0} selected event{(count == 1 ? string.Empty : "s")} of {View.TotalCount:N0}");
        }

        return View.IsFiltered
            ? string.Create(CultureInfo.InvariantCulture, $"{count:N0} of {View.TotalCount:N0} events (filtered)")
            : string.Create(CultureInfo.InvariantCulture, $"{count:N0} events");
    }
}
