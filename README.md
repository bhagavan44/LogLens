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

**Open a log** with *File ▸ Open* (Ctrl+O), by dropping the file on the window, from *File ▸ Recent
files* or the list on the start screen, or from the command line (`LogLens.exe C:\logs\app.txt`). Files
that another program is still writing open without blocking it. Large files load in the background:
the first rows show at once, the status bar shows the progress, and *Stop* ends the load and keeps
what was read. One log is shown at a time for now; tabs come in a later version.

LogLens runs once per user: opening another file from Explorer or the command line while it runs
opens the file in the running window and brings it to the front. When you start LogLens without a
file, it reopens the log that was open last time (with tail on if it was on).

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

**Go to** (Ctrl+G) jumps to an event number or to the first event at or after a time, and Ctrl+E /
Ctrl+Shift+E jump to the next / previous warning, error or fatal event.

| Shortcut | Action |
|---|---|
| Ctrl+O | Open a file |
| Ctrl+W | Close the log |
| ↑ ↓ PgUp PgDn Home End | Move through events |
| Shift / Ctrl + click or arrow | Select several events |
| Ctrl+A | Select all events |
| Ctrl+C | Copy the selected events as tab-separated text |
| Ctrl+Shift+C | Copy the selected events' original lines |
| Ctrl+F | Find |
| F3 / Shift+F3 (or Enter / Shift+Enter in the find box) | Next / previous match |
| Esc (in the find box) | Close the find bar |
| Ctrl+L | Go to the query bar; Enter applies at once, Esc clears it |
| Ctrl+G | Go to an event number or a time |
| Ctrl+E / Ctrl+Shift+E | Next / previous warning or error |

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
LogLens.exe [--data-dir <folder>] [--new-window] [<log file>]
```

- `--data-dir <folder>` keeps the recent files and the open log in `<folder>\LogLens` instead of
  `%APPDATA%\LogLens`, for a portable copy or for testing. Each folder is a separate LogLens.
- `--new-window` starts a separate LogLens instead of handing the file to the running one; it does not
  change what is reopened next time.

## Privacy

LogLens sends no telemetry and makes no network calls. Logs are only read, opened with sharing so
the app writing them is never blocked. The only file LogLens writes is `%APPDATA%\LogLens\state.json`
(the recent files and the open log).

## Contributing

Bug reports, format requests and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md)
to get started, and [SPEC.md](SPEC.md) for the scope and roadmap.

## License

[GNU General Public License v3.0](LICENSE)
