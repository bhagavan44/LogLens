using LogLens.Core.Streaming;

namespace LogLens.Core.Remote;

/// <summary>A folder or file listed while browsing a remote host. <see cref="Path"/> is what opening it takes (an <c>sftp://</c> location or a UNC path).</summary>
public sealed record RemoteEntry(string Name, string Path, bool IsFolder, long Length, DateTime LastWriteTime);

/// <summary>The folders of one connected remote host, as the browser sees them.</summary>
public interface IRemoteFolders
{
    /// <summary>The host's name, for headings.</summary>
    string DisplayName { get; }

    /// <summary>The folder to show first.</summary>
    string StartFolder { get; }

    /// <summary>The folder above <paramref name="folder"/>, or null at the top.</summary>
    string? Parent(string folder);

    /// <summary>The folders and files in <paramref name="folder"/> (unsorted, unfiltered).</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be read.</exception>
    /// <exception cref="IOException">The host could not be reached.</exception>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>File name patterns such as <c>*.txt;*.log</c>, as typed in the open-folder and remote dialogs.</summary>
public static class FilePatterns
{
    public const string Default = "*.txt;*.log;*.json;*.clef";

    /// <summary>The patterns in <paramref name="text"/> (separated by <c>;</c> or <c>,</c>); the defaults when there are none.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        var list = (string.IsNullOrWhiteSpace(text) ? Default : text)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        return list.Count > 0 ? list : Default.Split(';');
    }

    /// <summary>True when <paramref name="fileName"/> matches any of <paramref name="patterns"/> (case-insensitive, <c>*</c> and <c>?</c>).</summary>
    public static bool MatchesAny(IReadOnlyList<string> patterns, string fileName)
        => patterns.Any(p => RollPattern.Matches(p, fileName));
}
