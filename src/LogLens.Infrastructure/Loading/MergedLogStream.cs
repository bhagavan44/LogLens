using System.Runtime.CompilerServices;
using LogLens.Core.Formats;
using LogLens.Core.Merging;
using LogLens.Core.Models;
using LogLens.Core.Streaming;

namespace LogLens.Infrastructure.Loading;

/// <summary>
/// A merged view of several log files (FR-O4): reads every file (a few at a time), merges their events by time and delivers the result as one
/// batch. It is a snapshot: it does not follow the files, and <see cref="Refresh"/> does nothing.
/// </summary>
public sealed class MergedLogStream : ILogStream
{
    private const int MaxParallelFiles = 4;

    private readonly IReadOnlyList<string> _paths;
    private readonly LogFileLoader _loader;

    /// <param name="paths">The files, in the order ties are broken; duplicates are ignored.</param>
    public MergedLogStream(IReadOnlyList<string> paths, ILogFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_paths.Count == 0)
        {
            throw new ArgumentException("Choose at least one file to merge.", nameof(paths));
        }

        Format = format ?? LogFormats.Default;
        _loader = new LogFileLoader(Format);
    }

    public ILogFormat Format { get; }

    /// <summary>The files merged, as full paths.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>A merged view is a snapshot; there is nothing to follow.</summary>
    public bool Follow { get; set; }

    public bool FollowRolled { get; set; }

    public void Refresh()
    {
        // a snapshot: nothing to look for
    }

    /// <exception cref="MergeSourceException">One of the files could not be read; it names the file and wraps the reason.</exception>
    public async IAsyncEnumerable<StreamBatch> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // All reading and merging happens on the thread pool: this body may be iterated by the UI thread.
        var (merged, documents) = await Task.Run(() => LoadAndMergeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var bytes = documents.Sum(d => d.FileSizeBytes);
        var encodings = string.Join(" / ", documents.Select(d => d.EncodingName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal));

        yield return new ChunkBatch(
            merged.Text,
            new ParseDelta(0, merged.Events),
            bytes,
            bytes,
            InitialLoadComplete: true,
            FilePath: _paths[^1],
            EncodingName: encodings,
            MergedFiles: merged.Files);
    }

    private async Task<(MergedLog Merged, LogDocument[] Documents)> LoadAndMergeAsync(CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxParallelFiles);
        var documents = await Task.WhenAll(_paths.Select(async path =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _loader.LoadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
            {
                throw new MergeSourceException(path, ex);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return (LogMerger.Merge(documents), documents);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
