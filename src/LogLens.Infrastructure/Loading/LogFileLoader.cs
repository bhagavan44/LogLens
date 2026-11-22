using System.Diagnostics;
using System.Text;
using LogLens.Core.Formats;
using LogLens.Core.Loading;
using LogLens.Core.Models;

namespace LogLens.Infrastructure.Loading;

/// <summary>
/// Reads a log file into a <see cref="LogDocument"/> on a worker thread (SPEC §6.3): the file is opened with
/// <c>FileShare.ReadWrite | FileShare.Delete</c> so the app writing it is never blocked (FR-O5), read in chunks, decoded (FR-F6) and fed to
/// the format's incremental parser.
/// </summary>
public sealed class LogFileLoader : ILogDocumentLoader
{
    /// <summary>Bytes read per chunk.</summary>
    public const int DefaultChunkBytes = 1024 * 1024;

    private readonly ILogFormat _format;
    private readonly int _chunkBytes;

    public LogFileLoader(ILogFormat? format = null, int chunkBytes = DefaultChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 4);
        _format = format ?? LogFormats.Default;
        _chunkBytes = chunkBytes;
    }

    public Task<LogDocument> LoadAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // All I/O and parsing on the thread pool: callers are usually the UI thread.
        return Task.Run(() => Load(Path.GetFullPath(path), progress, cancellationToken), cancellationToken);
    }

    private LogDocument Load(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

        var document = new LogDocument(path, _format);
        var parser = _format.CreateParser(path);
        var length = stream.Length;
        var bytes = new byte[_chunkBytes];
        var read = ReadFully(stream, bytes, cancellationToken);

        var (encoding, preamble, name) = DetectEncoding(bytes.AsSpan(0, read));
        document.EncodingName = name;
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(_chunkBytes)];
        var offset = preamble;
        long total = 0;
        progress?.Report(0);

        while (read > 0)
        {
            total += read;
            var count = decoder.GetChars(bytes, offset, read - offset, chars, 0, flush: false);
            Append(new string(chars, 0, count));
            if (length > 0)
            {
                progress?.Report(Math.Min(1, (double)total / length));
            }

            offset = 0;
            read = ReadFully(stream, bytes, cancellationToken);
        }

        var tail = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        Append(new string(chars, 0, tail));
        document.Apply(parser.Complete());

        document.FileSizeBytes = total;
        document.LoadTime = clock.Elapsed;
        progress?.Report(1);
        return document;

        void Append(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            document.Text.Append(text);
            document.Apply(parser.Append(text, cancellationToken));
        }
    }

    /// <summary>Fills <paramref name="buffer"/> as far as the file allows; returns the number of bytes read (0 at the end).</summary>
    private static int ReadFully(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>
    /// FR-F6: UTF-8 (with or without BOM) by default, UTF-16 when its BOM says so. Invalid UTF-8 bytes decode to U+FFFD rather than failing:
    /// a damaged byte must never stop a log from opening.
    /// </summary>
    internal static (Encoding Encoding, int PreambleLength, string Name) DetectEncoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(false), 3, "UTF-8 with BOM");
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2, "UTF-16 LE");
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2, "UTF-16 BE");
        }

        return (new UTF8Encoding(false), 0, "UTF-8");
    }
}
