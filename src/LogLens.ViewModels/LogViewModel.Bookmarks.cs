using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Filtering;
using LogLens.Core.Models;
using LogLens.Core.State;

namespace LogLens.ViewModels;

/// <summary>One line of the bookmarks panel: a bookmarked event of the open log, with its note.</summary>
public sealed class BookmarkItem : ObservableObject
{
    private readonly LogViewModel _owner;
    private string _note;

    internal BookmarkItem(LogViewModel owner, LogEvent logEvent, Bookmark bookmark)
    {
        _owner = owner;
        Event = logEvent;
        Bookmark = bookmark;
        _note = bookmark.Note;
    }

    public LogEvent Event { get; }

    public Bookmark Bookmark { get; }

    public string TimeText => _owner.TimeDisplay.Time(Event);

    public string LevelText => LogText.LevelAbbreviation(Event.Level);

    public string Preview => LogText.FirstLine(Event.Message);

    /// <summary>"#1,234": the event number the detail pane shows.</summary>
    public string NumberText => string.Create(CultureInfo.InvariantCulture, $"#{Event.Id + 1:N0}");

    /// <summary>The file the event came from (shown in a merged view).</summary>
    public string Source => Path.GetFileName(Event.SourceFile ?? string.Empty);

    /// <summary>What a screen reader says for the line.</summary>
    public string AccessibleName => $"{NumberText}, {LogText.LevelName(Event.Level)}, {TimeText}, {Preview}{(Note.Length > 0 ? ", note: " + Note : string.Empty)}";

    /// <summary>The user's note; saved as it is typed.</summary>
    public string Note
    {
        get => _note;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _note, value))
            {
                _owner.SaveBookmarkNote(this, value);
            }
        }
    }
}

/// <summary>Bookmarks (FR-N1): toggled on an event, stepped through with F2 / Shift+F2, listed in a panel with notes, remembered per file.</summary>
public sealed partial class LogViewModel
{
    private const int PreviewLength = 160;

    private readonly BookmarkService _bookmarkService;
    private readonly HashSet<int> _bookmarkedIds = [];

    /// <summary>The bookmarks found in this log, in event order.</summary>
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = [];

    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>The bookmarks panel is shown.</summary>
    [ObservableProperty]
    public partial bool BookmarksPanelVisible { get; set; }

    /// <summary>Raised when the set of bookmarked events changed, so the grid redraws its markers.</summary>
    public event EventHandler? BookmarksChanged;

    public bool IsBookmarked(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return _bookmarkedIds.Contains(e.Id);
    }

    /// <summary>Adds or removes the bookmark on <paramref name="e"/> (Ctrl+F2); returns true when it was added.</summary>
    public bool ToggleBookmark(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if ((e.SourceFile ?? Document.FilePath) is not { } file)
        {
            return false; // not read from a file: nothing to remember it by
        }

        var preview = LogText.FirstLine(e.Message);
        return _bookmarkService.Toggle(new Bookmark
        {
            File = file,
            Offset = Document.OriginalOffset(e),
            Timestamp = e.Timestamp,
            Preview = preview.Length > PreviewLength ? preview[..PreviewLength] : preview,
        });
    }

    public void RemoveBookmark(BookmarkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _bookmarkService.Remove(item.Bookmark);
    }

    /// <summary>Removes every bookmark of the files shown in this log.</summary>
    public void ClearBookmarks() => _bookmarkService.Clear(Bookmarks.Select(b => b.Bookmark.File).ToList());

    /// <summary>The row of the next (or previous) bookmark the filters leave in the list, wrapping around (F2 / Shift+F2); -1 when there is none.</summary>
    public int NextBookmarkRow(int fromRow, bool forward)
    {
        var rows = Bookmarks.Select(b => View.IndexOf(b.Event)).Where(r => r >= 0).Order().ToArray();
        return MatchNavigator.Step(rows, fromRow, forward);
    }

    /// <summary>The row showing <paramref name="item"/>'s event, or -1 when the filters hide it.</summary>
    public int RowOf(BookmarkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return View.IndexOf(item.Event);
    }

    internal void SaveBookmarkNote(BookmarkItem item, string note) => _bookmarkService.SetNote(item.Bookmark, note);

    /// <summary>The timestamps are shown another way: the panel's lines are replaced so they show it too.</summary>
    private void RefreshBookmarkItems()
    {
        for (var i = 0; i < Bookmarks.Count; i++)
        {
            var item = Bookmarks[i];
            Bookmarks[i] = new BookmarkItem(this, item.Event, item.Bookmark with { Note = item.Note }); // the note may have been edited since
        }
    }

    private void OnBookmarkServiceChanged(object? sender, EventArgs e) => ResolveBookmarks(0);

    /// <summary>
    /// Matches the stored bookmarks with the events from <paramref name="firstIndex"/> on (everything before it is unchanged): after a load, an
    /// append, a re-read, or a bookmark added or removed in any tab.
    /// </summary>
    private void ResolveBookmarks(int firstIndex)
    {
        var changed = false;
        for (var i = Bookmarks.Count - 1; i >= 0 && Bookmarks[i].Event.Id >= firstIndex; i--)
        {
            _bookmarkedIds.Remove(Bookmarks[i].Event.Id);
            Bookmarks.RemoveAt(i);
            changed = true;
        }

        var events = Document.Events;
        if (_bookmarkService.Count > 0 && events.Count > firstIndex)
        {
            var byFile = new Dictionary<string, Dictionary<long, Bookmark>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in _bookmarkService.All)
            {
                if (!byFile.TryGetValue(b.File, out var map))
                {
                    byFile[b.File] = map = [];
                }

                map[b.Offset] = b;
            }

            string? lastFile = null;
            Dictionary<long, Bookmark>? lastMap = null;
            for (var i = Math.Max(0, firstIndex); i < events.Count; i++)
            {
                var e = events[i];
                var file = e.SourceFile ?? Document.FilePath;
                if (!string.Equals(file, lastFile, StringComparison.Ordinal))
                {
                    lastFile = file;
                    lastMap = file is not null && byFile.TryGetValue(file, out var found) ? found : null;
                }

                // The timestamp keeps a bookmark off another event when the file was replaced since.
                if (lastMap is not null && lastMap.TryGetValue(Document.OriginalOffset(e), out var bookmark) && bookmark.Timestamp == e.Timestamp)
                {
                    Bookmarks.Add(new BookmarkItem(this, e, bookmark));
                    _bookmarkedIds.Add(e.Id);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasBookmarks));
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
