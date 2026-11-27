# LogLens

A fast, offline log viewer for Windows. LogLens turns a text log into a filterable, searchable
grid, follows logs while they are being written, and never changes the files it opens.

> **Status: early development.** LogLens reads the files written by [Serilog](https://serilog.net):
> text with the default template
> (`{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}`) or your
> own output template, compact JSON (CLEF) and classic JSON. It also reads text logs written by
> [NLog](https://nlog-project.org) and [log4net](https://logging.apache.org/log4net/), and any text log
> you can describe with a regular expression, on this PC, a Windows share or an SSH server. See the
> [roadmap](SPEC.md#8-roadmap) for what comes next.

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

**Logs on other machines.** *File ▸ Open from remote host* (Ctrl+Shift+R, or *Remote host…* on the start
screen) lists your hosts: **SSH hosts**, such as Ubuntu servers, read over SFTP, and **Windows shares**
(`\\server\share\logs`). The hosts in your `~/.ssh/config` are listed too. Choose one and *Connect*, browse its
folders (click a part of the path to go back to that folder, the arrow after it to jump into one of its
sub-folders, or the empty space or Ctrl+L to type a path), filter and order the files, tick the logs you want
and open them in tabs or merge them. They tail, refresh, bookmark and reopen like local files; the status
shows *Reconnecting…* when a host stops answering, and the tail carries on where it was when it is back.
*Add host…* saves a host: for SSH the host name, port, user, optionally a private key file (OpenSSH, PEM or
PuTTY `.ppk`) and a folder to start in. *Duplicate…* starts a new host from the selected one's details, for
example another server with the same user and key. Type in the box above the list to show only the hosts whose
name, address, user or port contain every word typed.

- **Signing in:** LogLens tries the host's key file, otherwise `id_ed25519`, `id_ecdsa` or `id_rsa` in your
  `.ssh` folder, and asks for a key's passphrase or a password when it needs one. Passwords and passphrases
  are kept in memory only, so a dropped connection can be re-established, and are never saved. ssh-agent and
  Pageant keys are not used.
- **Host keys:** the first time you connect to a host that isn't in your `~/.ssh/known_hosts`, LogLens shows
  its fingerprint (`SHA256:…`). Compare it with `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the
  server before choosing *Trust and connect*. If a host's key changes, LogLens refuses to connect; after a
  server was reinstalled, use *Forget host key* in the host's settings (or fix `known_hosts`).
- **Paths:** a remote file is written `sftp://user@host[:port]/path/to/app.log`. You can pass that on the
  command line, and it is what the recent files and reopened tabs use; opening one from there connects first.
- **Windows shares** are read by Windows with your account. If a share needs other credentials, open it once
  in File Explorer first.

LogLens runs once per user: opening another file from Explorer or the command line while it runs
opens the file in a new tab of the running window and brings it to the front. When you start LogLens
without a file, it reopens the tabs that were open last time, merged views included (with tail on
where it was on); *Settings* can switch that off. To open logs straight from Explorer, add LogLens to
the *Open with* menu in *Settings*.

The **event list** shows one row per event: time (as logged, or in your time zone or UTC, see
[Settings](#settings)), level and the first line of the message.
A ⚠ marks events that carry an exception. Lines that don't match the format are shown as *unparsed*
rows instead of being dropped.

**Formats.** LogLens recognises each file's format from its first lines: Serilog text with the default
template, Serilog compact JSON (CLEF, as written by `CompactJsonFormatter`) and classic JSON
(`JsonFormatter`), NLog text with its default layout
(`${longdate}|${level:uppercase=true}|${logger}|${message:withexception=true}`), log4net text with the
usual pattern (`%date [%thread] %-5level %logger - %message%newline`), plus any custom formats you add in
[Settings](#settings). The status bar names the format. If a file is read wrongly, pick its format under
*View ▸ Format*; the tab is read again with it, and the choice is remembered with the tab. *Detect
automatically* goes back to detection. Logs that name the logging class (JSON, NLog's `${logger}`,
log4net's `%logger`, or a template with `{SourceContext}`) get a **Context** column showing it; for every
format it is the `SourceContext` property. Level names such as `TRACE`, `INFO`, `WARN`, `NOTICE` or
`CRITICAL` are understood, also in the query bar (`@l >= 'WARN'`).

The **detail pane** on the right shows the selected event: the full message, the exception with its
stack trace (*Copy exception*), and the raw text exactly as in the file, with the file name, line
and event number above. For structured logs the **Properties** tab shows the message template and every
property; the buttons next to a value (or right-click it) filter the list to that value or exclude it,
and *Copy value* copies it.

### Finding things

The **filter bar** above the list narrows it down; all filters combine, the status bar shows how many
events are left (*120 of 5,000 events*), and *Clear filters* removes them all at once.

- **Levels**: one button per level with its count; click to show or hide that level, or pick a
  minimum such as *Warning and above*. Unparsed lines get a button of their own.
- **Time**: the last 5 minutes … 24 hours of the log (counted back from its last event), ± 30 s …
  1 hour around the selected event, or a custom *From* / *To* range. A time without a date, such as
  `08:15`, uses the date of the first event.
- **Query** (Ctrl+L): a small expression language over `@l` (level), `@t` (timestamp), `@m`
  (message), `@x` (exception) and, in structured logs, `@mt` (message template) and properties by name,
  for example `@l >= 'Warning' and @m like '%payment%'`, `exists(@x) and @t >= '2026-11-13 10:00'` or
  `Elapsed > 500 and SourceContext like 'Shop.%'`. A dotted name reaches into a structure
  (`Http.Status = 500`); a property whose name is not a plain word is written `@p['Request Id']`.
  Operators: `= != < <= > >=`, `in [...]`, `like` (`%` and `_`), `contains`, `startswith`,
  `endswith`, `matches` (regular expression), `exists()`, `and`, `or`, `not` and parentheses. Values are
  text in quotes, numbers, `true`, `false` and `null`. Text comparisons ignore case; a number matches a
  property holding that number; an event without the property matches no comparison except `= null`. A mistake is shown under the bar with its position, and the last valid
  query stays applied until you fix it. Click **?** for the syntax.
- **Quick filters**: right-click an event to show only, or hide, events like it (the same message
  with different numbers, ids or quoted values), show only or hide its level, or show the events
  within ± 30 s of it. Active quick filters show as chips you can click to remove.
- **Saved filters**: *Saved ▸ Save current filter…* keeps the query and the level buttons under a name;
  pick it from the *Saved* menu of any log to apply it again (the tick shows the one in force), or delete
  it there.
- **Hide rules** leave noise out of every log, like a filter that is always on: health checks, a chatty
  component, all *Verbose* events. Write them in *Settings* with the query syntax, for example
  `@m startswith 'Health check'`. While rules apply, the filter bar shows *Hiding noise (2 rules)*;
  click it to see the hidden events for a moment, and again to hide them. *Clear filters* leaves the
  rules on.

The **find bar** (Ctrl+F) highlights matches in the list and counts them (*3 of 120*). Options: match
case, whole word, regular expression, and whether to search exception text too. *Only matches*
narrows the list to the matching events, with *Context ±* showing that many events around each.

Regular expressions (in the find bar, `matches` and `like` in the query bar, hide rules and custom
formats) never hang a log: most run on a linear-time engine. One that needs lookarounds or
backreferences and keeps timing out on a log is reported as *too slow* and isn't applied, like a
mistake; a hide rule that is too slow is skipped with a message.

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
*N new events* button that takes you back down. *Pause* (Ctrl+Shift+P) holds new events back so the list stays still,
and shows them all when you resume. Filters and the find bar apply to new events too.

A **rolling log** (`log20261113.txt` → `log20261114.txt`, or `app-001.log` → `app-002.log`) is followed
to its next file in the same folder; turn this off with *View ▸ Follow the next file of a rolling log*.
A file that gets shorter or is replaced is read again from the start, and a file that disappears is
waited for until it is back.

With tail off, **Refresh** (F5) reads what was written since the file was last read.

| Shortcut | Action |
|---|---|
| Ctrl+T | Tail on / off |
| Ctrl+Shift+P | Pause / resume the tail |
| Ctrl+Shift+R | Open from remote host |
| F5 | Refresh (with tail off) |

Files are read as UTF-8, or UTF-16 when they start with a byte-order mark.

### Analyse and export

**Timeline** (*View ▸ Timeline*) draws the events over time above the list, stacked by level, so a burst
of errors stands out. Drag across it, or click a bar, to show only that time range; the range is outlined
and the bars outside it are dimmed. With the keyboard, focus it and use the arrow keys (Shift to extend)
and Enter; Esc clears the range. The bars follow the other filters, so you can still pick another range
after choosing one.

**Statistics** (*View ▸ Statistics*, or the *Statistics* tab of the detail pane) summarise the events
currently listed: how many, per level, the time they span, and the most frequent messages and
exceptions. Numbers, ids and quoted values count as one, so *Order 41 shipped* and *Order 42 shipped* are
one line. Click a line to show only those events.

**Export** (*File ▸ Export…*, Ctrl+S) writes the events in the list, or just the selected ones, as CSV,
JSON lines (with the message template and properties of structured events), a self-contained HTML page,
the original lines, or a Markdown table. Events are always written
oldest first. In a CSV, text that starts with `=`, `+`, `-` or `@` gets a leading `'` so a spreadsheet
never runs it as a formula. Right-click ▸ *Copy as Markdown table* puts the selected events on the clipboard,
ready for a ticket.

Other *View* options:

- **Newest events first** lists the newest event at the top; new events then arrive there.
- **Wrap long messages** shows a long message on two lines instead of cutting it off. Every row stays the
  same height.
- **Time column** shows each event's time since the previous event, or since the selected one
  (*+120 ms*, *+3.204 s*), instead of its timestamp. This setting is not remembered.
- **Columns:** right-click the list header to hide or show the Time, Level, Context and Source columns, and
  drag a header edge (or focus it and use the arrow keys; Home resets) to resize. The layout, the row order,
  wrapping and the timeline are remembered.

| Shortcut | Action |
|---|---|
| Ctrl+S | Export the events |

### Settings

*File ▸ Settings…* (Ctrl+,) holds everything that is not a View option. Nothing changes until you press
*Save*.

- **Appearance**: light or dark theme, or follow Windows; the font and size of the log text (Inter comes
  with LogLens; type the name of any installed font); comfortable or compact rows.
- **Timestamps**: show times as logged, in this PC's time zone or in UTC, in any .NET date and time
  format (for example `HH:mm:ss.fff`; a preview shows the result). The time filter, the query bar's
  `@t`, the timeline and *Go to* read times in the zone shown, so what you type matches what you see.
  The detail pane always adds the offset.
- **Opening and following**: reopen last time's tabs at start-up (on by default), and start every file
  you open with tail on.
- **Formats**: add the format your app writes its text logs in, under a name, and say what it is written
  as. Paste a line from the log to check what the format reads from it. Custom formats are tried when a
  file opens and offered under *View ▸ Format*.
  - A **Serilog output template**, for example
    `{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}`. Any property token
    (`{SourceContext}`, `{ThreadId}`, `{RequestId}` …) becomes a property you can filter on; `{Properties}` is
    not supported.
  - An **NLog layout**, copied from `NLog.config`, for example
    `${longdate} [${threadid}] ${level:uppercase=true} ${logger}: ${message} ${exception:format=tostring}`.
    `${longdate}`, `${date}`, `${shortdate}` and `${time}` are the time (with `format=` and `universalTime=true`),
    `${logger}` is `SourceContext`, and other renderers become properties (`${event-properties:item=RequestId}`
    is `RequestId`, `${threadid}` is `ThreadId`).
  - A **log4net conversion pattern**, for example `%date{ABSOLUTE} [%thread] %-5level %logger - %message%newline`.
    `%date` / `%utcdate` (`{ISO8601}`, `{ABSOLUTE}`, `{DATE}` or a .NET format) are the time, `%logger` is
    `SourceContext`, and other conversions become properties (`%thread` is `Thread`, `%property{RequestId}` is
    `RequestId`).
  - A **regular expression** for the first line of an event, with named groups: `timestamp` (or `date` and
    `time`) and `message` are required, `level` and `exception` are optional, `logger` is `SourceContext`, and
    any other group becomes a property, for example
    `^\[(?<timestamp>[^\]]+)\] (?<level>\w+)\s+(?<logger>\S+) :: (?<message>.*)$`. The time can be written in
    any common way.

  In every format, lines that don't start an event (stack traces, wrapped messages) belong to the event
  above them.
- **Hide rules**: add, name, switch off or delete them. A rule with a mistake is marked and has to be
  fixed (or switched off) before saving.
- **Open with**: *Register* adds LogLens to Explorer's *Open with* menu for `.log` and `.clef` (and, if you
  tick them, `.txt` and `.json`) for your Windows user only: no administrator rights, and your default
  programs stay as they are. *Remove* takes it out again. After moving or updating the exe, register
  again.
- **Settings file**: *Export…* saves your settings to a file and *Import…* loads one, to set up another
  PC the same way. The column layout stays with each PC.

*Help ▸ Keyboard shortcuts* (F1) lists every shortcut, and *Help ▸ Show the first-run hints again*
brings back the two tips shown the first time a log is open.

| Shortcut | Action |
|---|---|
| Ctrl+, | Settings |
| F1 | Keyboard shortcuts |

### Command line

```
LogLens.exe [--data-dir <folder>] [--new-window] [--merge] [<log file> ...]
```

Each file opens in a tab of its own.

- `--merge` opens the files as one merged view instead, ordered by time with a *Source* column.
- `--data-dir <folder>` keeps the settings, recent files, bookmarks, open tabs and diagnostics in
  `<folder>\LogLens` instead of `%APPDATA%\LogLens` and `%LOCALAPPDATA%\LogLens`, for a portable copy
  or for testing. Each folder is a separate LogLens.
- `--new-window` starts a separate LogLens instead of handing the file to the running one; it does not
  change what is reopened next time.

## Privacy

LogLens sends no telemetry. Its only network connections are to the SSH hosts you connect to, to read
logs (Windows shares are reached by Windows itself). Logs are only read, opened with sharing so the app
writing them is never blocked; remote files are opened for reading only. LogLens writes these files of its
own:

- `%APPDATA%\LogLens\state.json`: the recent files, bookmarks, open tabs and the SSH host keys you accepted.
- `%APPDATA%\LogLens\settings.json`: your settings, hide rules, saved filters, the column layout and your
  remote hosts (never a password).
- `%LOCALAPPDATA%\LogLens\logs\`: LogLens's own diagnostics log (what it did and what went wrong, never
  the content of your logs; the last 14 days) and a report for each unexpected error (the last 10).
  *Settings ▸ Open diagnostics folder* shows them; attach one to a bug report if you like. Nothing is
  sent anywhere.

*Open with* writes to the registry under `HKEY_CURRENT_USER\Software\Classes` only when you press
*Register*, and *Remove* deletes it again. Anything else is a file you chose to export. Your
`~/.ssh/config` and `~/.ssh/known_hosts` are read, never written.

## Contributing

Bug reports, format requests and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md)
to get started, and [SPEC.md](SPEC.md) for the scope and roadmap.

## License

[GNU General Public License v3.0](LICENSE)
