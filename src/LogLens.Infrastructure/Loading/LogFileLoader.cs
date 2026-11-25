using System.Diagnostics;
using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.Streaming;

namespace LogLens.Infrastructure.Loading;

/// <summary>
/// Reads a whole log file into a finished <see cref="LogDocument"/>, without following it. This is <see cref="FileLogStream"/> run to the end of
/// the file, so a one-off load and the app's progressive load and tail share one reading and parsing path.
/// </summary>
public sealed class LogFileLoader
{
    private readonly ILogFormat _format;
    private readonly int _chunkBytes;

    /// <param name="format">The format to read files with; null detects each file's format among the built-in ones (FR-F1).</param>
    /// <param name="chunkBytes">Largest read at once.</param>
    public LogFileLoader(ILogFormat? format = null, int chunkBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 4);
        _format = format ?? LogFormats.Detect;
        _chunkBytes = chunkBytes;
    }

    /// <summary>Reads and parses <paramref name="path"/> off the calling thread. <paramref name="progress"/> reports the fraction read (0 … 1).</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">Its folder does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be read with the current user's rights.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <param name="path">A local or UNC path, or an <c>sftp://</c> location read through <paramref name="fileSystem"/>.</param>
    /// <param name="progress">Reports the fraction read.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <param name="fileSystem">Where the file is read from; the local file system when null. A remote host that drops fails the load.</param>
    public async Task<LogDocument> LoadAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        ILogFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        fileSystem ??= LocalLogFileSystem.Instance;
        var fullPath = fileSystem is LocalLogFileSystem ? Path.GetFullPath(path) : path;

        await using var stream = new FileLogStream(fullPath, new StreamOptions
        {
            Format = _format,
            ChunkBytes = _chunkBytes,
            FirstChunkBytes = _chunkBytes,
            UseWatcher = false,
            CompleteAtEndOfInitialLoad = true,
        }, fileSystem);

        var document = new LogDocument(fullPath, _format);
        progress?.Report(0);

        await foreach (var batch in stream.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (batch)
            {
                case ChunkBatch chunk:
                    document.Text.Append(chunk.Text);
                    document.Apply(chunk.Delta);
                    document.EncodingName = chunk.EncodingName;
                    document.FileSizeBytes = chunk.FileLength;
                    if (chunk.Format is { } detected)
                    {
                        document.Format = detected;
                    }
                    if (chunk.FileLength > 0)
                    {
                        progress?.Report(Math.Min(1, (double)chunk.BytesRead / chunk.FileLength));
                    }

                    break;

                case ResetBatch:
                    document.Clear(); // the file changed while it was read: start over with what is there now
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1);
        document.LoadTime = clock.Elapsed;
        return document;
    }
}
