using System.Text;
using LogLens.Core.Models;

namespace LogLens.Core.Merging;

/// <summary>Where one file's text starts in a merged document's text.</summary>
/// <param name="Start">Character offset of the file's first character.</param>
/// <param name="Path">The file.</param>
public readonly record struct FileSegment(long Start, string Path);

/// <summary>Several logs combined into one: their texts one after another, and their events in time order.</summary>
/// <param name="Text">The sources' texts, each starting on a new line.</param>
/// <param name="Events">Every event of every source, ordered by time, numbered from 0, offsets pointing into <paramref name="Text"/>.</param>
/// <param name="Files">Where each source's text starts in <paramref name="Text"/>, in the order the sources were given.</param>
public sealed record MergedLog(string Text, IReadOnlyList<LogEvent> Events, IReadOnlyList<FileSegment> Files);

/// <summary>
/// Combines several loaded logs into one chronologically ordered list (merge view, FR-O4): the logs of several services, or the files of a rolling
/// set such as <c>log20261111.txt</c>, <c>log20261112.txt</c>, ….
/// </summary>
public static class LogMerger
{
    /// <summary>Merges <paramref name="sources"/> by timestamp, compared as instants, so files logged in different offsets interleave correctly.</summary>
    /// <remarks>
    /// A k-way merge that keeps each file's own order: a file whose clock steps backwards is not re-sorted, and lines that did not parse stay
    /// directly after the event they follow. Events with the same timestamp come out in the order the files were given. Every event's
    /// <see cref="LogEvent.SourceFile"/> names the file it came from.
    /// </remarks>
    public static MergedLog Merge(IReadOnlyList<LogDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("There is nothing to merge.", nameof(sources));
        }

        var text = new StringBuilder(checked((int)sources.Sum(s => s.Text.Length + 1)));
        var files = new FileSegment[sources.Count];
        var total = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            files[i] = new FileSegment(text.Length, source.FilePath ?? $"source {i + 1}");
            var length = (int)source.Text.Length;
            if (length > 0)
            {
                text.Append(source.Text.Substring(0, length));
                if (text[^1] != '\n')
                {
                    text.Append('\n'); // the next file starts on a line of its own, so line numbers stay per file
                }
            }

            total += source.Events.Count;
        }

        var events = new List<LogEvent>(total);
        var cursor = new int[sources.Count];
        while (true)
        {
            // The source whose next event is earliest; ties go to the earlier file. Text that did not parse has no time and goes first.
            var best = -1;
            var bestTime = DateTimeOffset.MaxValue;
            for (var i = 0; i < sources.Count; i++)
            {
                if (cursor[i] >= sources[i].Events.Count)
                {
                    continue;
                }

                var head = sources[i].Events[cursor[i]];
                var time = head.IsUnparsed ? DateTimeOffset.MinValue : head.Timestamp;
                if (best < 0 || time < bestTime)
                {
                    best = i;
                    bestTime = time;
                }
            }

            if (best < 0)
            {
                break;
            }

            // The event and the unparsed lines after it go together, so they are never separated.
            var list = sources[best].Events;
            var first = true;
            while (cursor[best] < list.Count && (first || list[cursor[best]].IsUnparsed))
            {
                var e = list[cursor[best]++];
                events.Add(e with { Id = events.Count, Offset = e.Offset + files[best].Start, SourceFile = files[best].Path });
                first = false;
            }
        }

        return new MergedLog(text.ToString(), events, files);
    }
}
