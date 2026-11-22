using LogLens.Core.Formats;

namespace LogLens.Core.Models;

/// <summary>
/// An open log: its decoded text plus the events parsed from it. Built by applying <see cref="ParseDelta"/>s as text arrives, so loading a
/// whole file and (later) tailing it share one path.
/// </summary>
/// <remarks>Built on one thread, then handed over and only read; never mutated by two threads at once.</remarks>
public sealed class LogDocument
{
    private readonly int[] _levelCounts = new int[Enum.GetValues<LogLevel>().Length];

    public LogDocument(string? filePath, ILogFormat format)
    {
        FilePath = filePath;
        Format = format ?? throw new ArgumentNullException(nameof(format));
    }

    /// <summary>The file this document was read from (null when it was not read from a file).</summary>
    public string? FilePath { get; }

    public string FileName => FilePath is null ? "(untitled)" : Path.GetFileName(FilePath);

    /// <summary>The format the events were parsed with.</summary>
    public ILogFormat Format { get; }

    /// <summary>The decoded file contents; <see cref="LogEvent.Offset"/> / <see cref="LogEvent.Length"/> index into it.</summary>
    public TextStore Text { get; } = new();

    public LogEventList Events { get; } = new();

    /// <summary>Name of the text encoding the file was decoded with (FR-F6), e.g. "UTF-8".</summary>
    public string EncodingName { get; set; } = string.Empty;

    /// <summary>Size of the file in bytes as read.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>How long the load took (zero until it finishes).</summary>
    public TimeSpan LoadTime { get; set; }

    /// <summary>Number of <see cref="LogLevel.Unparsed"/> events.</summary>
    public int UnparsedCount => _levelCounts[(int)LogLevel.Unparsed];

    /// <summary>The original text of an event exactly as in the file (FR-D3).</summary>
    public string GetRaw(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Text.Substring(e.Offset, e.Length);
    }

    /// <summary>The 1-based line of the file on which <paramref name="e"/> starts (FR-D4).</summary>
    public int LineNumberOf(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Text.LineNumberAt(e.Offset);
    }

    /// <summary>Number of events with <paramref name="level"/>.</summary>
    public int CountOf(LogLevel level) => _levelCounts[(int)level];

    /// <summary>Removes every event with an id ≥ <see cref="ParseDelta.FirstId"/>, then appends the delta's events.</summary>
    public void Apply(ParseDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.FirstId < 0 || delta.FirstId > Events.Count)
        {
            throw new InvalidOperationException($"Delta starts at event {delta.FirstId} but the document only has {Events.Count}.");
        }

        for (var i = delta.FirstId; i < Events.Count; i++)
        {
            _levelCounts[(int)Events[i].Level]--;
        }

        Events.TruncateTo(delta.FirstId);
        foreach (var e in delta.Events)
        {
            if (e.Id != Events.Count)
            {
                throw new InvalidOperationException($"Event id {e.Id} does not follow {Events.Count - 1}.");
            }

            Events.Add(e);
            _levelCounts[(int)e.Level]++;
        }
    }
}
