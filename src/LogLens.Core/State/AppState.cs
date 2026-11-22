namespace LogLens.Core.State;

/// <summary>What LogLens remembers between runs that is not a preference (SPEC §6.5, <c>state.json</c>): recent files and the open logs.</summary>
public sealed record AppState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Most recently opened files, newest first (FR-O1).</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>The logs open when LogLens last ran, reopened on the next start (FR-P3).</summary>
    public SessionState Session { get; init; } = new();
}

/// <summary>The open logs in order and which one was in front. v0.3 shows one log at a time; tabs arrive in v0.4.</summary>
public sealed record SessionState
{
    public IReadOnlyList<SessionTab> Tabs { get; init; } = [];

    public int Selected { get; init; }
}

/// <summary>One open log.</summary>
public sealed record SessionTab
{
    public string? Path { get; init; }

    /// <summary>Tail was on (FR-T1).</summary>
    public bool Follow { get; init; }

    /// <summary>The entry names a file (a hand-edited file may hold empty ones).</summary>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Path);
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
