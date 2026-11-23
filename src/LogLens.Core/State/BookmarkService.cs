namespace LogLens.Core.State;

/// <summary>
/// A bookmarked event (FR-N1). It is identified by its file and its character offset in that file, which stays put while the file only grows;
/// the timestamp guards against the file having been replaced since (other events at the same offsets).
/// </summary>
public sealed record Bookmark
{
    public string File { get; init; } = string.Empty;

    public long Offset { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The first line of the event's message, so a list of bookmarks means something on its own.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>The user's note on the bookmark.</summary>
    public string Note { get; init; } = string.Empty;

    public bool IsSameEvent(Bookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Offset == other.Offset && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The bookmarks of every file (FR-N1), saved to <c>state.json</c> on every change. Saving never throws into the UI.</summary>
public sealed class BookmarkService
{
    /// <summary>More bookmarks than this are not kept: the oldest go first.</summary>
    public const int MaxBookmarks = 1000;

    private readonly IStateStore _store;
    private readonly Action<Exception>? _onSaveError;
    private List<Bookmark> _bookmarks;

    public BookmarkService(IStateStore store, Action<Exception>? onSaveError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onSaveError = onSaveError;
        _bookmarks = (store.Load().Bookmarks ?? []).Where(b => b is not null && !string.IsNullOrWhiteSpace(b.File)).TakeLast(MaxBookmarks).ToList();
    }

    /// <summary>Raised when a bookmark was added or removed (not when only a note changed), so every open log can update its markers.</summary>
    public event EventHandler? Changed;

    /// <summary>Every bookmark, in the order they were added.</summary>
    public IReadOnlyList<Bookmark> All => _bookmarks;

    public int Count => _bookmarks.Count;

    /// <summary>Adds the bookmark, or removes it when that event is already bookmarked. Returns true when it was added.</summary>
    public bool Toggle(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var existing = _bookmarks.FindIndex(b => b.IsSameEvent(bookmark));
        var next = new List<Bookmark>(_bookmarks);
        if (existing >= 0)
        {
            next.RemoveAt(existing);
        }
        else
        {
            next.Add(bookmark);
            if (next.Count > MaxBookmarks)
            {
                next.RemoveAt(0);
            }
        }

        Commit(next, raiseChanged: true);
        return existing < 0;
    }

    public void Remove(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var next = _bookmarks.Where(b => !b.IsSameEvent(bookmark)).ToList();
        if (next.Count != _bookmarks.Count)
        {
            Commit(next, raiseChanged: true);
        }
    }

    /// <summary>Removes every bookmark of <paramref name="files"/>.</summary>
    public void Clear(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var next = _bookmarks.Where(b => !set.Contains(b.File)).ToList();
        if (next.Count != _bookmarks.Count)
        {
            Commit(next, raiseChanged: true);
        }
    }

    /// <summary>Changes the note of a bookmark. Does not raise <see cref="Changed"/>, so a list being edited is not rebuilt under the cursor.</summary>
    public void SetNote(Bookmark bookmark, string note)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        note ??= string.Empty;
        var index = _bookmarks.FindIndex(b => b.IsSameEvent(bookmark));
        if (index < 0 || _bookmarks[index].Note == note)
        {
            return;
        }

        var next = new List<Bookmark>(_bookmarks) { [index] = _bookmarks[index] with { Note = note } };
        Commit(next, raiseChanged: false);
    }

    private void Commit(List<Bookmark> next, bool raiseChanged)
    {
        _bookmarks = next;
        try
        {
            _store.Save(_store.Load() with { Bookmarks = next });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onSaveError?.Invoke(ex);
        }

        if (raiseChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
