using LogLens.Core.Streaming;

namespace LogLens.Infrastructure.Loading;

/// <summary>
/// Where <see cref="FileLogStream"/> reads from: the local file system (also UNC shares) or an SSH host over SFTP. Paths are whatever the
/// file system understands — a Windows path, or an <c>sftp://</c> location. Every member may be called from a worker thread.
/// </summary>
public interface ILogFileSystem
{
    /// <summary>True when a <see cref="FileSystemWatcher"/> can report changes; otherwise only polling notices new data.</summary>
    bool SupportsWatching { get; }

    /// <summary>Opens the file for reading without blocking writers, renamers or deleters. The stream must support seeking and <see cref="Stream.Length"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">Its folder does not exist.</exception>
    /// <exception cref="RemoteUnavailableException">The host can't be reached right now; try again later.</exception>
    /// <exception cref="IOException">The file is briefly unavailable (locked, mid-rotation); try again later.</exception>
    Stream OpenRead(string path);

    /// <summary>When the file was created (local) or last written (SFTP has no creation time); used to tell which rolled file is newer.</summary>
    DateTime GetCreatedUtc(string path);

    /// <summary>The files in <paramref name="folder"/> with their creation (or last-write) times.</summary>
    IEnumerable<RollCandidate> ListFiles(string folder);

    /// <summary>The folder that holds <paramref name="path"/>.</summary>
    string? GetFolder(string path);

    /// <summary>The path of <paramref name="name"/> in <paramref name="folder"/>.</summary>
    string Combine(string folder, string name);
}

/// <summary>The host of a remote file can't be reached at the moment (connection lost, timed out); reading is retried.</summary>
public sealed class RemoteUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Local files and Windows shares.</summary>
public sealed class LocalLogFileSystem : ILogFileSystem
{
    public static LocalLogFileSystem Instance { get; } = new();

    private LocalLogFileSystem()
    {
    }

    public bool SupportsWatching => true;

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

    public DateTime GetCreatedUtc(string path) => File.GetCreationTimeUtc(path);

    public IEnumerable<RollCandidate> ListFiles(string folder)
        => Directory.EnumerateFiles(folder).Select(f => new RollCandidate(Path.GetFileName(f), File.GetCreationTimeUtc(f)));

    public string? GetFolder(string path) => Path.GetDirectoryName(path);

    public string Combine(string folder, string name) => Path.Combine(folder, name);
}
