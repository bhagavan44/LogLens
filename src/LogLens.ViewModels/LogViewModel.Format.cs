using LogLens.Core.Filtering;
using LogLens.Core.Formats;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>One row of the properties table in the detail pane (FR-D5).</summary>
/// <param name="Name">The property's name.</param>
/// <param name="Value">Its value.</param>
public sealed record PropertyRow(string Name, LogValue Value)
{
    /// <summary>The value as shown: text without quotes, structures and sequences as compact JSON.</summary>
    public string ValueText => Value.ToString();
}

/// <summary>
/// The log's format (FR-F1) and what it brings: the message template and properties of the selected event (FR-D5), filters made from a
/// property value, and the Context column.
/// </summary>
public sealed partial class LogViewModel
{
    /// <summary>The format the log is read with (the detected one once the first lines are read).</summary>
    public ILogFormat Format => Document.Format;

    /// <summary>The format the user chose for this log (FR-F1); null when it is detected.</summary>
    public ILogFormat? ChosenFormat { get; init; }

    /// <summary>The format's events have properties or a message template: the detail pane shows its Properties tab.</summary>
    public bool HasStructuredFields => (Document.Format.Fields & (LogFields.Properties | LogFields.MessageTemplate)) != 0;

    /// <summary>The format's events name their logging class (<c>SourceContext</c>): the grid can show a Context column.</summary>
    public bool HasSourceContext => Document.Format.Fields.HasFlag(LogFields.SourceContext);

    /// <summary>The format became known (it was detected from the first lines) or changed.</summary>
    public event EventHandler? FormatChanged;

    // ----- detail pane (FR-D5) -----

    public bool HasMessageTemplate => SelectedEvent?.MessageTemplate is not null;

    /// <summary>The selected event's message template, unrendered.</summary>
    public string MessageTemplateText => SelectedEvent?.MessageTemplate ?? string.Empty;

    public bool HasProperties => SelectedEvent?.Properties is { Count: > 0 };

    /// <summary>The selected event's properties, in the order the log wrote them.</summary>
    public IReadOnlyList<PropertyRow> SelectedProperties
        => SelectedEvent?.Properties is { } properties ? [.. properties.Select(p => new PropertyRow(p.Key, p.Value))] : [];

    /// <summary>What the Properties tab says when there is nothing to list.</summary>
    public string NoPropertiesText => SelectedEvent switch
    {
        null => string.Empty,
        { IsUnparsed: true } => "This line did not parse as an event, so it has no properties.",
        _ => "This event has no properties.",
    };

    /// <summary>
    /// FR-D5: adds "<paramref name="row"/>'s property = its value" to the query (or "not (…)" with <paramref name="exclude"/>) and applies it at
    /// once. Events without the property stay listed by an exclusion.
    /// </summary>
    public void FilterToProperty(PropertyRow row, bool exclude)
    {
        ArgumentNullException.ThrowIfNull(row);
        QueryText = QueryWriter.And(QueryText, QueryWriter.PropertyEquals(row.Name, row.Value, exclude));
        ApplyQueryNow();
    }

    /// <summary>Takes the format a stream batch says the text is parsed with.</summary>
    private void UpdateFormat(ILogFormat? format)
    {
        if (format is null || ReferenceEquals(format, Document.Format))
        {
            return;
        }

        Document.Format = format;
        OnPropertyChanged(nameof(Format));
        OnPropertyChanged(nameof(HasStructuredFields));
        OnPropertyChanged(nameof(HasSourceContext));
        OnPropertyChanged(nameof(StatusText));
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }
}
