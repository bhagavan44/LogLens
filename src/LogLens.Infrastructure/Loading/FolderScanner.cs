namespace LogLens.Infrastructure.Loading;

/// <summary>A file offered by Open folder.</summary>
public sealed record LogFileInfo(string Path, string Name, long Length, DateTime LastWriteTime);

/// <summary>Lists the log files in a folder that match a pattern (Open folder, FR-O3).</summary>
public static class FolderScanner
{
    public const string DefaultPatterns = Core.Remote.FilePatterns.Default;

    /// <summary>More files than this are not listed: a folder with tens of thousands of files is not a log folder.</summary>
    public const int MaxFiles = 5000;

    private static readonly char[] InvalidPatternChars = [.. Path.GetInvalidFileNameChars().Where(c => c is not '*' and not '?')];

    /// <summary>
    /// The files in <paramref name="folder"/> matching any of <paramref name="patterns"/> (wildcards separated by <c>;</c> or <c>,</c>; the
    /// default ones when empty), sorted by path, which is also time order for date-stamped rolling logs such as <c>log20261113.txt</c>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be read.</exception>
    /// <exception cref="IOException">The folder can't be read.</exception>
    public static IReadOnlyList<LogFileInfo> Enumerate(string folder, string? patterns = null, bool includeSubfolders = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var list = ParsePatterns(patterns);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System,
        };

        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"{folder} does not exist.");
        }

        var found = new Dictionary<string, LogFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in list)
        {
            foreach (var info in directory.EnumerateFiles(pattern, options))
            {
                found.TryAdd(info.FullName, new LogFileInfo(info.FullName, info.Name, info.Length, info.LastWriteTime));
                if (found.Count >= MaxFiles)
                {
                    break;
                }
            }
        }

        return [.. found.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The wildcards in <paramref name="patterns"/>; the default ones when it holds none that can be used.</summary>
    public static IReadOnlyList<string> ParsePatterns(string? patterns)
    {
        var list = (patterns ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0 && p.IndexOfAny(InvalidPatternChars) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count > 0 ? list : DefaultPatterns.Split(';');
    }
}
