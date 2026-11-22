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

**Open a log** with *File ▸ Open* (Ctrl+O), by dropping the file on the window, or from the command line
(`LogLens.exe C:\logs\app.txt`). Files that another program is still writing open without blocking it.
One log is shown at a time for now; tabs come in a later version.

The **event list** shows one row per event: time (as logged), level and the first line of the message.
A ⚠ marks events that carry an exception. Lines before the first event that don't match the format
are shown as *unparsed* rows instead of being dropped.

The **detail pane** on the right shows the selected event: the full message, the exception with its
stack trace (*Copy exception*), and the raw text exactly as in the file, with the file name, line
and event number above.

| Shortcut | Action |
|---|---|
| Ctrl+O | Open a file |
| Ctrl+W | Close the log |
| ↑ ↓ PgUp PgDn Home End | Move through events |
| Shift / Ctrl + click or arrow | Select several events |
| Ctrl+A | Select all events |
| Ctrl+C | Copy the selected events as tab-separated text |
| Ctrl+Shift+C | Copy the selected events' original lines |

Files are read as UTF-8, or UTF-16 when they start with a byte-order mark.

## Privacy

LogLens sends no telemetry and makes no network calls. Logs are only read, opened with sharing so
the app writing them is never blocked.

## Contributing

Bug reports, format requests and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md)
to get started, and [SPEC.md](SPEC.md) for the scope and roadmap.

## License

[GNU General Public License v3.0](LICENSE)
