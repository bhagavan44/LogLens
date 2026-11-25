using Renci.SshNet;
using LogLens.Core.Remote;
using LogLens.Core.Streaming;
using LogLens.Infrastructure.Loading;

namespace LogLens.Infrastructure.Remote;

/// <summary>Reads <c>sftp://</c> files through an <see cref="SftpConnection"/>. Strictly read-only: files are opened for reading only.</summary>
public sealed class SftpLogFileSystem(SftpConnection connection) : ILogFileSystem
{
    public bool SupportsWatching => false;

    public Stream OpenRead(string path)
    {
        var remote = PosixPath(path);
        var stream = connection.Run(c => c.Open(remote, FileMode.Open, FileAccess.Read));
        return new GuardedStream(stream, connection);
    }

    public DateTime GetCreatedUtc(string path)
    {
        var remote = PosixPath(path);
        return connection.Run(c => c.GetAttributes(remote).LastWriteTimeUtc); // SFTP has no creation time
    }

    public IEnumerable<RollCandidate> ListFiles(string folder)
    {
        var remote = PosixPath(folder);
        return connection.Run(c => c.ListDirectory(remote)
            .Where(f => f.IsRegularFile)
            .Select(f => new RollCandidate(f.Name, f.LastWriteTimeUtc))
            .ToList());
    }

    public string? GetFolder(string path)
        => Parse(path) is var location ? location.WithPath(RemoteLocation.Parent(location.Path)).ToString() : null;

    public string Combine(string folder, string name)
    {
        var location = Parse(folder);
        return location.WithPath(RemoteLocation.Combine(location.Path, name)).ToString();
    }

    private static RemoteLocation Parse(string path)
        => RemoteLocation.TryParse(path, out var location) ? location : throw new ArgumentException($"'{path}' is not an sftp:// location.", nameof(path));

    private static string PosixPath(string path) => Parse(path).Path;

    /// <summary>Passes reads through, turning SFTP and network errors into the exceptions <see cref="FileLogStream"/> handles.</summary>
    private sealed class GuardedStream(Stream inner, SftpConnection connection) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => Guard(() => inner.Length);

        public override long Position
        {
            get => Guard(() => inner.Position);
            set => Guard(() => inner.Position = value);
        }

        public override int Read(byte[] buffer, int offset, int count) => Guard(() => inner.Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => Guard(() => inner.Seek(offset, origin));

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    inner.Dispose();
                }
                catch (Exception)
                {
                    // closing a handle on a dropped connection fails; the handle is gone with the session anyway
                }
            }

            base.Dispose(disposing);
        }

        private T Guard<T>(Func<T> operation)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (connection.Translate(ex) is { } translated)
            {
                throw translated;
            }
        }
    }
}

/// <summary>Browses the folders of a connected SSH host.</summary>
public sealed class SftpFolders(SftpConnection connection, RemoteHost host) : IRemoteFolders
{
    public string DisplayName => host.DisplayName;

    public string StartFolder => host.StartFolder switch
    {
        "" or "~" => connection.HomeFolder,
        var f when f.StartsWith("~/", StringComparison.Ordinal) => RemoteLocation.Combine(connection.HomeFolder, f[2..]),
        var f => f,
    };

    public string? Parent(string folder) => folder.TrimEnd('/').Length == 0 ? null : RemoteLocation.Parent(folder);

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
        {
            var location = new RemoteLocation(connection.Target.UserName, connection.Target.Host, connection.Target.Port, folder);
            try
            {
                return connection.Run(c =>
                {
                    var entries = new List<RemoteEntry>();
                    foreach (var f in c.ListDirectory(folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (f.Name is "." or "..")
                        {
                            continue;
                        }

                        var isFolder = f.IsDirectory;
                        var isFile = f.IsRegularFile;
                        var length = f.Length;
                        var modified = f.LastWriteTime;
                        if (f.IsSymbolicLink)
                        {
                            // A link to a folder (common for /var/log/app → /srv/app/logs) should open like one.
                            try
                            {
                                var target = c.GetAttributes(f.FullName);
                                isFolder = target.IsDirectory;
                                isFile = target.IsRegularFile;
                                length = target.Size;
                                modified = target.LastWriteTime;
                            }
                            catch (Exception ex) when (ex is Renci.SshNet.Common.SftpPathNotFoundException or Renci.SshNet.Common.SftpPermissionDeniedException)
                            {
                                continue; // a dangling link, or one we may not follow
                            }
                        }

                        if (isFolder)
                        {
                            entries.Add(new RemoteEntry(f.Name, f.FullName, true, 0, modified));
                        }
                        else if (isFile)
                        {
                            entries.Add(new RemoteEntry(f.Name, location.WithPath(f.FullName).ToString(), false, length, modified));
                        }
                    }

                    return entries;
                });
            }
            catch (FileNotFoundException ex)
            {
                throw new DirectoryNotFoundException($"{folder} doesn't exist on {connection.Target.Host}.", ex);
            }
        }, cancellationToken);
}

/// <summary>Browses a Windows share (<c>\\server\share\folder</c>); Windows handles the network and the sign-in.</summary>
public sealed class ShareFolders(RemoteHost host) : IRemoteFolders
{
    public string DisplayName => host.DisplayName;

    public string StartFolder => host.Address;

    public string? Parent(string folder) => Path.GetDirectoryName(folder.TrimEnd('\\')) is { Length: > 2 } parent && parent.TrimStart('\\').Contains('\\') ? parent : null;

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
        {
            var entries = new List<RemoteEntry>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            var directory = new DirectoryInfo(folder);
            foreach (var d in directory.EnumerateDirectories("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new RemoteEntry(d.Name, d.FullName, true, 0, d.LastWriteTime));
            }

            foreach (var f in directory.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new RemoteEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
            }

            return entries;
        }, cancellationToken);
}
