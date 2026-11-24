namespace LogLens.Core.State;

/// <summary>
/// What LogLens remembers between runs that is not a preference (SPEC §6.5, <c>state.json</c>): recent files, bookmarks and the open logs.
/// </summary>
public sealed record AppState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Most recently opened files, newest first (FR-O1).</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>The logs open when LogLens last ran, reopened on the next start (FR-P3).</summary>
    public SessionState Session { get; init; } = new();

    /// <summary>Bookmarked events of every file, in the order they were added (FR-N1).</summary>
    public IReadOnlyList<Bookmark> Bookmarks { get; init; } = [];
}

/// <summary>The open tabs in order and which one was in front.</summary>
public sealed record SessionState
{
    public IReadOnlyList<SessionTab> Tabs { get; init; } = [];

    public int Selected { get; init; }
}

/// <summary>One open tab: a file (<see cref="Path"/>) or a merged view of several (<see cref="MergedFiles"/>).</summary>
public sealed record SessionTab
{
    public string? Path { get; init; }

    /// <summary>Tail was on (FR-T1).</summary>
    public bool Follow { get; init; }

    /// <summary>The files of a merged view (FR-O4), or null for a single file.</summary>
    public IReadOnlyList<string>? MergedFiles { get; init; }

    /// <summary>Id of the format the user chose for the tab (FR-F1); null when the format is detected.</summary>
    public string? Format { get; init; }

    /// <summary>The entry names a file, or at least two files to merge (a hand-edited file may hold empty ones).</summary>
    public bool IsValid()
        => MergedFiles is null
            ? !string.IsNullOrWhiteSpace(Path)
            : MergedFiles.Count(f => !string.IsNullOrWhiteSpace(f)) >= 2;
}

/// <summary>Reads and writes <see cref="AppState"/>. Implemented in Infrastructure (a JSON file).</summary>
public interface IStateStore
{
    /// <summary>Loads the state. Never throws: a missing or unreadable file gives an empty state.</summary>
    AppState Load();

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    void Save(AppState state);
}

/// <summary>An <see cref="IStateStore"/> that keeps the state in memory only: nothing outlives the process.</summary>
public sealed class InMemoryStateStore : IStateStore
{
    private AppState _state = new();

    public AppState Load() => _state;

    public void Save(AppState state) => _state = state ?? throw new ArgumentNullException(nameof(state));
}
