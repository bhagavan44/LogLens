using LogLens.Core.Formats;

namespace LogLens.Core.Streaming;

/// <summary>What a log stream is doing; shown next to the Tail button.</summary>
public enum StreamState
{
    /// <summary>Reading the file's existing content.</summary>
    Loading,

    /// <summary>Everything read; not following (tail is off).</summary>
    Idle,

    /// <summary>Everything read; watching for new data.</summary>
    Following,

    /// <summary>The file has gone (deleted, or renamed away by a log rotator); looking for it again until it is back.</summary>
    WaitingForFile,
}

/// <summary>One unit of work delivered by an <see cref="ILogStream"/>. Batches arrive in order and are applied on the UI thread.</summary>
public abstract record StreamBatch;

/// <summary>New text read from the file, already parsed. The text is appended to the document's text <b>before</b> the delta is applied.</summary>
/// <param name="Text">The characters just read (events refer to them by absolute offset).</param>
/// <param name="Delta">The change to the event list.</param>
/// <param name="BytesRead">How far into the current file the stream has read.</param>
/// <param name="FileLength">Size of the current file when it was read.</param>
/// <param name="InitialLoadComplete">True on the batch that ends the first read of the file: everything that was there is now listed.</param>
/// <param name="FilePath">The file this text came from (it changes when a rolling log moves on to its next file).</param>
/// <param name="EncodingName">The encoding that file is decoded with (FR-F6).</param>
public sealed record ChunkBatch(
    string Text,
    ParseDelta Delta,
    long BytesRead,
    long FileLength,
    bool InitialLoadComplete,
    string FilePath,
    string EncodingName) : StreamBatch;

/// <summary>The file was truncated or replaced: everything listed so far is stale, and the file is read again from the start (FR-T3).</summary>
public sealed record ResetBatch(string Reason) : StreamBatch;

/// <summary>A rolling log moved on to <paramref name="FilePath"/>; its events continue the list (FR-T3).</summary>
public sealed record SwitchedBatch(string FilePath) : StreamBatch;

/// <summary>The stream changed state.</summary>
public sealed record StatusBatch(StreamState State, string? Message = null) : StreamBatch;

/// <summary>
/// A source of parsed log events: the existing content first, in chunks so the first rows appear quickly, then — while <see cref="Follow"/>
/// is on — whatever is appended later, following a rolling log to its next file (FR-T1, FR-T3).
/// </summary>
public interface ILogStream : IAsyncDisposable
{
    /// <summary>The format the events are parsed with.</summary>
    ILogFormat Format { get; }

    /// <summary>Keep watching for new data once the existing content has been read. May change at any time.</summary>
    bool Follow { get; set; }

    /// <summary>While following, switch to the next file of a rolling log (e.g. the next day's) once it appears.</summary>
    bool FollowRolled { get; set; }

    /// <summary>
    /// Looks for new data once, now, even while <see cref="Follow"/> is off (Refresh, F5; FR-T7): what was appended is delivered as usual and
    /// a truncated or replaced file is reported with a <see cref="ResetBatch"/>. Safe to call at any time and from any thread.
    /// </summary>
    void Refresh();

    /// <summary>
    /// Produces batches until <paramref name="cancellationToken"/> is cancelled or the stream is disposed. All reading and parsing happens on
    /// worker threads; the enumerator only hands over finished batches.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist when the stream starts.</exception>
    /// <exception cref="DirectoryNotFoundException">Its folder does not exist when the stream starts.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be read with the current user's rights.</exception>
    IAsyncEnumerable<StreamBatch> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Opens <see cref="ILogStream"/>s. Implemented in Infrastructure (files); faked in view-model tests.</summary>
public interface ILogStreamFactory
{
    /// <summary>A stream over <paramref name="path"/>. Nothing is read until <see cref="ILogStream.ReadAsync"/> is enumerated.</summary>
    ILogStream Open(string path);
}
