# LogLens

A fast, offline log viewer for Windows. LogLens turns a text log into a filterable, searchable
grid, follows logs while they are being written, and never changes the files it opens.

> **Status: early development.** The first release reads log files written by
> [Serilog](https://serilog.net)'s file sink with its default text template:
> `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}`.
> More formats (CLEF / JSON, NLog, log4net, custom templates) are planned; see the
> [roadmap](SPEC.md#8-roadmap).

## Install

LogLens will ship as a single portable exe: no installer, no administrator rights, and no .NET or
Windows App SDK to install first. Download `LogLens-<version>-win-x64.exe` (or `-win-arm64.exe`)
from the [releases page](https://github.com/bhagavan44/LogLens/releases) once the first version is
out.

Requires Windows 10 version 1809 or later, or Windows 11.

## Using LogLens

**Open logs** with *File ▸ Open* (Ctrl+O, several files at once if you like), by dropping files on the
window, from *File ▸ Recent files* or the list on the start screen, or from the command line
(`LogLens.exe C:\logs\app.txt`). Each file opens in a **tab** of its own in the title bar; opening a file
that is already open brings its tab to the front. Ctrl+Tab / Ctrl+Shift+Tab switch tabs, Ctrl+W closes
one, drag a tab to reorder, and right-click a tab for *Close other tabs*. Files that another program is
still writing open without blocking it. Large files load in the background: the first rows show at
once, the status bar shows the progress, and *Stop* ends the load and keeps what was read.

**Open folder** (Ctrl+Shift+O, or drop a folder on the window) lists the log files in a folder
(`*.txt;*.log;*.json;*.clef` by default; change the pattern, or include subfolders) so you can pick
the ones you want and open them in tabs, or merge them.

A **merged view** (*Open folder ▸ Merge into one view*, or *File ▸ Merge files into one view*) puts the
events of several files into one list ordered by time, with a *Source* column naming each event's
file: the logs of several services, or the files of a rolling log, side by side. Files logged in
different time zones interleave correctly. A merged view is a snapshot: it does not follow the files.

LogLens runs once per user: opening another file from Explorer or the command line while it runs
opens the file in a new tab of the running window and brings it to the front. When you start LogLens
without a file, it reopens the tabs that were open last time, merged views included (with tail on
where it was on).

The **event list** shows one row per event: time (as logged), level and the first line of the message.
A ⚠ marks events that carry an exception. Lines before the first event that don't match the format
are shown as *unparsed* rows instead of being dropped.

The **detail pane** on the right shows the selected event: the full message, the exception with its
stack trace (*Copy exception*), and the raw text exactly as in the file, with the file name, line
and event number above.

### Finding things

The **filter bar** above the list narrows it down; all filters combine, the status bar shows how many
events are left (*120 of 5,000 events*), and *Clear filters* removes them all at once.

- **Levels**: one button per level with its count; click to show or hide that level, or pick a
  minimum such as *Warning and above*. Unparsed lines get a button of their own.
- **Time**: the last 5 minutes … 24 hours of the log (counted back from its last event), ± 30 s …
  1 hour around the selected event, or a custom *From* / *To* range. A time without a date, such as
  `08:15`, uses the date of the first event.
- **Query** (Ctrl+L): a small expression language over `@l` (level), `@t` (timestamp), `@m`
  (message) and `@x` (exception), for example
  `@l >= 'Warning' and @m like '%payment%'` or `exists(@x) and @t >= '2026-11-13 10:00'`.
  Operators: `= != < <= > >=`, `in [...]`, `like` (`%` and `_`), `contains`, `startswith`,
  `endswith`, `matches` (regular expression), `exists()`, `and`, `or`, `not` and parentheses. Text
  comparisons ignore case. A mistake is shown under the bar with its position, and the last valid
  query stays applied until you fix it. Click **?** for the syntax.
- **Quick filters**: right-click an event to show only, or hide, events like it (the same message
  with different numbers, ids or quoted values), show only or hide its level, or show the events
  within ± 30 s of it. Active quick filters show as chips you can click to remove.

The **find bar** (Ctrl+F) highlights matches in the list and counts them (*3 of 120*). Options: match
case, whole word, regular expression, and whether to search exception text too. *Only matches*
narrows the list to the matching events, with *Context ±* showing that many events around each.

**Find all events containing** (Ctrl+Shift+F, or right-click an event) follows an order number,
request id or user name through a log: it lists only the events whose message or exception contains
the text, optionally with a few events around each, and clears the other filters first so nothing
related stays hidden. Select text in the detail pane first and it is filled in for you. In a merged
view it covers every file.

**Go to** (Ctrl+G) jumps to an event number or to the first event at or after a time, and Ctrl+E /
Ctrl+Shift+E jump to the next / previous warning, error or fatal event.

**Bookmarks** mark events to come back to: Ctrl+F2 (or right-click ▸ *Bookmark*) toggles one, and F2 /
Shift+F2 step through them. *View ▸ Bookmarks panel* lists them with a note you can type for each; click
one to go to it. Bookmarks are remembered per file, so they are there the next time you open it, also
in a merged view of that file.

| Shortcut | Action |
|---|---|
| Ctrl+O | Open files, each in a tab |
| Ctrl+Shift+O | Open folder |
| Ctrl+W | Close the tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| ↑ ↓ PgUp PgDn Home End | Move through events |
| Shift / Ctrl + click or arrow | Select several events |
| Ctrl+A | Select all events |
| Ctrl+C | Copy the selected events as tab-separated text |
| Ctrl+Shift+C | Copy the selected events' original lines |
| Ctrl+F | Find |
| F3 / Shift+F3 (or Enter / Shift+Enter in the find box) | Next / previous match |
| Esc (in the find box) | Close the find bar |
| Ctrl+L | Go to the query bar; Enter applies at once, Esc clears it |
| Ctrl+Shift+F | Find all events containing a text |
| Ctrl+G | Go to an event number or a time |
| Ctrl+E / Ctrl+Shift+E | Next / previous warning or error |
| Ctrl+F2 | Bookmark the event (or remove its bookmark) |
| F2 / Shift+F2 | Next / previous bookmark |

### Live logs

**Tail** (Ctrl+T, or the *Tail* button) shows new events as they are written, within a second. The list
follows the newest event while it is in view; scroll up to read and the new events are counted in an
*N new events* button that takes you back down. *Pause* holds new events back so the list stays still,
and shows them all when you resume. Filters and the find bar apply to new events too.

A **rolling log** (`log20261113.txt` → `log20261114.txt`, or `app-001.log` → `app-002.log`) is followed
to its next file in the same folder; turn this off with *View ▸ Follow the next file of a rolling log*.
A file that gets shorter or is replaced is read again from the start, and a file that disappears is
waited for until it is back.

With tail off, **Refresh** (F5) reads what was written since the file was last read.

| Shortcut | Action |
|---|---|
| Ctrl+T | Tail on / off |
| F5 | Refresh (with tail off) |

Files are read as UTF-8, or UTF-16 when they start with a byte-order mark.

### Command line

```
LogLens.exe [--data-dir <folder>] [--new-window] [<log file> ...]
```

Each file opens in a tab of its own.

- `--data-dir <folder>` keeps the recent files, bookmarks and open tabs in `<folder>\LogLens` instead of
  `%APPDATA%\LogLens`, for a portable copy or for testing. Each folder is a separate LogLens.
- `--new-window` starts a separate LogLens instead of handing the file to the running one; it does not
  change what is reopened next time.

## Privacy

LogLens sends no telemetry and makes no network calls. Logs are only read, opened with sharing so
the app writing them is never blocked. The only file LogLens writes is `%APPDATA%\LogLens\state.json`
(the recent files, bookmarks and open tabs).

## Contributing

Bug reports, format requests and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md)
to get started, and [SPEC.md](SPEC.md) for the scope and roadmap.

## License

[GNU General Public License v3.0](LICENSE)
