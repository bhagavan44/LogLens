using LogLens.Core.Formats;

namespace LogLens.Core.Models;

/// <summary>What <see cref="LogDocument.Apply"/> did to the event list, so views can update only the affected rows.</summary>
/// <param name="FirstChangedIndex">Index of the first event that was removed or added.</param>
/// <param name="Removed">How many events were removed from the end before appending.</param>
/// <param name="Added">How many events were appended.</param>
public readonly record struct DocumentChange(int FirstChangedIndex, int Removed, int Added);

/// <summary>
/// An open log: its decoded text plus the events parsed from it. It grows by applying <see cref="ParseDelta"/>s as text arrives, so loading a
/// file and tailing it share one path; a rolling log's next file continues the same document (FR-T3).
/// </summary>
/// <remarks>
/// Not thread-safe. The app mutates it on the UI thread only, and never while a background pass (filter, search) reads it: stream batches
/// wait until the pass has finished.
/// </remarks>
public sealed class LogDocument
{
    private readonly int[] _levelCounts = new int[Enum.GetValues<LogLevel>().Length];
    private readonly List<(long Start, string? Path)> _files = [];

    public LogDocument(string? filePath, ILogFormat format)
    {
        FilePath = filePath;
        Format = format ?? throw new ArgumentNullException(nameof(format));
        _files.Add((0, filePath));
    }

    /// <summary>The file that was opened (null when the document was not read from a file).</summary>
    public string? FilePath { get; }

    /// <summary>The file being read now: after a rolling log moved on (FR-T3), its latest file; otherwise <see cref="FilePath"/>.</summary>
    public string? CurrentFilePath => _files[^1].Path;

    /// <summary>Name of <see cref="CurrentFilePath"/>.</summary>
    public string FileName => CurrentFilePath is null ? "(untitled)" : Path.GetFileName(CurrentFilePath);

    /// <summary>The format the events were parsed with.</summary>
    public ILogFormat Format { get; }

    /// <summary>The decoded file contents; <see cref="LogEvent.Offset"/> / <see cref="LogEvent.Length"/> index into it.</summary>
    public TextStore Text { get; } = new();

    public LogEventList Events { get; } = new();

    /// <summary>Name of the text encoding the file was decoded with (FR-F6), e.g. "UTF-8".</summary>
    public string EncodingName { get; set; } = string.Empty;

    /// <summary>Size of the current file in bytes as last read.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>How long the first read took (zero until it finishes).</summary>
    public TimeSpan LoadTime { get; set; }

    /// <summary>Number of <see cref="LogLevel.Unparsed"/> events.</summary>
    public int UnparsedCount => _levelCounts[(int)LogLevel.Unparsed];

    /// <summary>The original text of an event exactly as in the file (FR-D3).</summary>
    public string GetRaw(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Text.Substring(e.Offset, e.Length);
    }

    /// <summary>The 1-based line, within its own file, on which <paramref name="e"/> starts (FR-D4).</summary>
    public int LineNumberOf(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var start = FileStartOf(e.Offset);
        return Text.LineNumberAt(e.Offset) - (start == 0 ? 0 : Text.LineNumberAt(start) - 1);
    }

    /// <summary>Number of events with <paramref name="level"/>.</summary>
    public int CountOf(LogLevel level) => _levelCounts[(int)level];

    /// <summary>
    /// The text that follows belongs to <paramref name="path"/>, the next file of a rolling log (FR-T3): its events continue this document and
    /// their line numbers count from that file's start.
    /// </summary>
    public void BeginFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_files[^1].Start == Text.Length)
        {
            _files[^1] = (Text.Length, path); // nothing was read from the previous one
        }
        else
        {
            _files.Add((Text.Length, path));
        }
    }

    /// <summary>Removes every event with an id ≥ <see cref="ParseDelta.FirstId"/>, then appends the delta's events.</summary>
    public DocumentChange Apply(ParseDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.FirstId < 0 || delta.FirstId > Events.Count)
        {
            throw new InvalidOperationException($"Delta starts at event {delta.FirstId} but the document only has {Events.Count}.");
        }

        var removed = Events.Count - delta.FirstId;
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

        return new DocumentChange(delta.FirstId, removed, delta.Events.Count);
    }

    /// <summary>Forgets all text and events: the file was truncated or replaced and is read again from its start (FR-T3).</summary>
    public void Clear()
    {
        Text.Clear();
        Events.Clear();
        Array.Clear(_levelCounts);
        var current = CurrentFilePath;
        _files.Clear();
        _files.Add((0, current));
        LoadTime = TimeSpan.Zero;
    }

    private long FileStartOf(long offset)
    {
        for (var i = _files.Count - 1; i > 0; i--)
        {
            if (_files[i].Start <= offset)
            {
                return _files[i].Start;
            }
        }

        return 0;
    }
}
