using LogLens.Core.Models;

namespace LogLens.Core.Loading;

/// <summary>Reads a log into a <see cref="LogDocument"/>. Implemented in Infrastructure (files); faked in view-model tests.</summary>
public interface ILogDocumentLoader
{
    /// <summary>
    /// Reads and parses <paramref name="path"/> off the calling thread. <paramref name="progress"/> reports the fraction read (0 … 1).
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">Its folder does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be read with the current user's rights.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<LogDocument> LoadAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
