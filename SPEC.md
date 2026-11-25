# LogLens — Product & Technical Spec

**Status:** Draft v0.1 · **Platform:** Windows 10 (1809+) / Windows 11 desktop, WinUI 3 · **Date:** 2026-11-22

## 1. Overview

LogLens is a Windows desktop application for opening, searching, filtering and live-tailing log
files. It turns raw text logs into a fast, structured, filterable grid so developers and support
engineers can diagnose problems without grepping through text in Notepad.

LogLens is built to support many log formats through pluggable parsers. **The first release
(v1.0) supports one format: Serilog's default plain-text template** (§2). Further formats follow
in later releases (§8).

### 1.1 Goals

- Open log files of several formats, with automatic format detection once more than one exists.
- Stay responsive on large files (hundreds of MB, hundreds of thousands of events).
- Make the common questions cheap to answer: *what errors happened, when, and what led up to them?*
- Show exceptions and (for structured formats) properties clearly, not as an unreadable text blob.
- Follow logs live while an app is still writing to them.
- Work fully offline with no telemetry, no services to install and no admin rights.

### 1.2 Non-goals (v1)

- Not a log aggregation server (no Seq / Elasticsearch / Splunk replacement, no network ingestion).
- No editing, rewriting or deleting of log files — the viewer is strictly read-only.
- No cross-platform support (macOS / Linux) in v1.
- No alerting or dashboards.
- No masking / redaction of log content.
- No on-disk index or multi-GB file support in v1 (§6.3).

### 1.3 Target users

| User | Need |
|---|---|
| Developer | Debug a failing run locally; follow a request across log lines; inspect exceptions |
| Support / QA engineer | Open a log sent by a customer, find errors in a time window, export evidence |
| Ops engineer | Tail a rolling log on a server (via RDP or a mapped share) |

### 1.4 Constraints for v1

| Topic | Decision | Consequence |
|---|---|---|
| File size | Typically **1 MB – 30 MB** per file (≈ 10 k – 250 k events) | Whole file is parsed into memory; ~100 MB is the stress target (§5, §6.3). |
| Format | **Serilog plain text, default template** only | One built-in parser behind the format interface; no template editor in v1. |
| Distribution | Portable single-file exe | No installer and no signing certificate. MSIX + winget later. |
| Data sensitivity | No masking | Exports include content verbatim. |

> **What the default template contains.** It writes only the timestamp, level, rendered message
> and exception — no `SourceContext`, structured properties or message template. Features that
> need those fields arrive with the structured formats (§2, §8).

## 2. Log formats

Every format is a parser behind one interface in `LogLens.Core` (§6.2). The grid, filters, search,
statistics and export work on the common `LogEvent` model and never special-case a format.

| # | Format | Example | Release |
|---|---|---|---|
| F1 | **Serilog plain text** (default output template) | `2026-11-13 10:15:32.123 +05:30 [INF] Order 42 shipped` | **v1.0** |
| F2 | Serilog compact JSON / CLEF | `{"@t":"2026-11-13T04:45:32.123Z","@mt":"Order {Id} shipped","@l":"Information","Id":42}` | v1.x structured formats |
| F3 | Serilog classic JSON | `{"Timestamp":"…","Level":"Information","MessageTemplate":"…","Properties":{…}}` | v1.x structured formats |
| F4 | Custom Serilog output templates | `{Timestamp:HH:mm:ss} {Level:u3} {SourceContext} {Message}` | v1.x structured formats |
| F5 | NLog text (default layout, or a custom layout) | `2026-11-13 08:00:04.8884\|WARN\|Shop.Payments\|Retry 2/3` | v1.x other loggers |
| F6 | log4net text (the usual conversion pattern, or a custom one) | `2026-11-13 08:00:04,888 [12] WARN  Shop.Payments - Retry 2/3` | v1.x other loggers |
| F7 | Custom regular expression with named groups | `(?<timestamp>…) (?<level>\w+) (?<message>.*)` | v1.x other loggers |
| F8 | Microsoft.Extensions.Logging console and other loggers | — | Later |

### 2.1 Detection and parsing

- **FR-F1** Each file is read by one format, detected per file from its first ~50 non-empty lines
  (the format recognising the most lines wins; on a tie JSON formats, then custom formats, then NLog, then
  log4net, then the default Serilog template; the default template when nothing matches). The user can
  override it per tab
  (*View ▸ Format*); the choice is remembered with the tab. A live log that starts empty is detected
  from its first complete line; the next file of a rolling log keeps the detected format.
- **FR-F2** Plain-text parsing is template-driven: a Serilog output template, an NLog layout or a
  log4net conversion pattern is compiled to a regular expression for the first line of an event, or the
  user gives that expression directly (named groups `timestamp` or `date` + `time`, `message`, optional
  `level` and `exception`). Serilog's default template, NLog's default layout and log4net's usual pattern
  are built in; users add their own formats of any of these kinds in Settings, with a test line that shows
  what a format reads. Any property token (`{SourceContext}`, `${threadid}`, `%property{RequestId}`, a
  named group …) becomes a property of the event; the logger (`${logger}`, `%logger`, group `logger`) is
  the `SourceContext` property. Serilog's `{Properties}` is not supported.
- **FR-F3** **Multi-line events** (messages with newlines, stack traces) attach to the preceding
  event: any line that doesn't match the start-of-event pattern is a continuation line.
- **FR-F4** Level mapping accepts full names (`Information`) and abbreviations (`INF`, `I`),
  case-insensitive, and other loggers' names (`TRACE`, `INFO`, `WARN`, `NOTICE`, `SEVERE`, `CRITICAL`,
  `trce`, `fail` …). Unknown levels are kept verbatim and shown with a neutral colour.
- **FR-F5** Lines that fail to parse are never dropped silently: they are shown as an *Unparsed*
  event carrying the raw text, which can be hidden.
- **FR-F6** Encoding: UTF-8 (with or without BOM) by default; UTF-16 detected by BOM.

## 3. Functional requirements

> **Plain-text applicability.** For F1 files the grid shows Time, Level, Message and Source (file);
> the query bar supports `@l`, `@t`, `@m`, `@x` and text operators; statistics group by a
> *normalised message* (numbers, GUIDs and quoted values replaced by placeholders). Property-based
> features (properties pane, property filters, a *Source Context* column) need a format that writes
> properties: JSON, or a template, layout, pattern or expression that names some.

### 3.1 Opening files

- **FR-O1** Open via File ▸ Open, drag-and-drop, command-line argument / *Open with*, and a recent
  files list.
- **FR-O2** Open several files at once, each in its own tab.
- **FR-O3** **Open folder**: list matching files (default `*.txt;*.log;*.json;*.clef`) and pick one
  or many.
- **FR-O4** **Merge view**: combine several files into one chronologically ordered stream with a
  *Source* column.
- **FR-O5** Files being written by another process open without error (`FileShare.ReadWrite |
  Delete`). The viewer never takes an exclusive lock.

### 3.2 Event list

- **FR-G1** Virtualised grid, one row per event, columns *Time, Level, Message, Source*. Columns
  can be hidden and resized; the layout is remembered.
- **FR-G2** Level shown as a coloured badge with text: Verbose, Debug, Information, Warning, Error,
  Fatal. Colours meet WCAG AA in light and dark themes.
- **FR-G3** Oldest first by default; *Newest first* as an option.
- **FR-G4** Multi-select; Ctrl+C copies rows as tab-separated text, Ctrl+Shift+C as raw lines.
- **FR-G5** Timestamps shown *as logged*, *local* or *UTC*, with a selectable format; relative
  times (since the previous / selected event) as a toggle.
- **FR-G6** *Wrap long messages* toggle. Every row keeps the same height.
- **FR-G7** *Go to event #* and *Go to time*.

### 3.3 Event detail pane

- **FR-D1** Full message (selectable, copyable).
- **FR-D2** Exception panel with the stack trace; *Copy exception*.
- **FR-D3** Raw tab with the original line(s) exactly as in the file.
- **FR-D4** Metadata: file name, line / byte offset, event number.
- **FR-D5** *(structured formats)* Message template and a properties table with *Filter to this
  value* / *Exclude this value*.

### 3.4 Filtering and search

- **FR-S1** **Level filter**: per-level toggles with live counts, and a *minimum level* pick.
- **FR-S2** **Time filter**: last N minutes, ± N seconds around the selected event, custom range.
- **FR-S3** **Find**: case, whole word, regex, include exception text; matches highlighted; F3 /
  Shift+F3; *only matches* with ± context events.
- **FR-S4** **Query bar** with a small expression language: `= != < <= > >=`, `and or not`, `in`,
  `like`, `contains`, `startswith`, `exists()`, fields `@l @t @m @x @mt`, and properties by name
  (`RequestId`, a dotted path such as `Http.Status`, or `@p['Any name']`). Errors shown inline with
  their position; an invalid query keeps the last valid one applied.
- **FR-S5** **Quick filters** from the context menu: show / hide events like this one, show only /
  hide this level, ±30 s around this event.
- **FR-S6** **Hide rules**: saved queries whose matches are left out of every log, with a one-click
  bypass.
- **FR-S7** **Saved filters**: name and recall a query and level choice.
- **FR-S8** Filtering and search run off the UI thread, are cancellable and never block the UI.

### 3.5 Live tail

- **FR-T1** *Tail* toggle appends new events as they are written (file watcher plus polling).
- **FR-T2** Auto-scroll while at the newest event; scrolling away shows an *"N new events"* button.
- **FR-T3** **Rolling files**: follow the next file of a rolling log (`log20261113.txt` →
  `log20261114.txt`); a truncated or replaced file is re-read from the start.
- **FR-T4** Active filters apply to incoming events.
- **FR-T5** Pause / resume; events keep buffering while paused.
- **FR-T6** A partial last line is held back until its newline arrives.
- **FR-T7** With tail off, *Refresh* (F5) reads what was written since the file was opened.

### 3.6 Navigation

- **FR-N1** **Bookmarks** (Ctrl+F2, F2 / Shift+F2), listed in a side panel, remembered per file.
- **FR-N2** **Timeline** above the grid: event counts over time by level; drag to filter a range.
- **FR-N3** Next / previous warning or error (Ctrl+E / Ctrl+Shift+E).
- **FR-N4** **Find all events containing** a text, across every file of a merged view.

### 3.7 Statistics

- **FR-X1** Panel for the filtered set: totals, per-level counts, time span, most frequent messages
  and exceptions (grouped by normalised message).
- **FR-X2** Clicking a row filters to it.

### 3.8 Export

- **FR-E1** Export the filtered events (or the selection) to CSV, JSON lines, self-contained HTML,
  the original lines, or a Markdown table.

### 3.9 Settings

- **FR-P1** Theme: Light, Dark, Follow system.
- **FR-P2** Font family and size for log text (Inter bundled by default), row density.
- **FR-P3** Timestamp zone and format, tail by default, reopen last session's tabs.
- **FR-P4** Hide rules, saved filters, export / import settings for another PC.
- **FR-P5** Per-user *Open with* registration for `.log`, `.clef` (optionally `.txt`, `.json`).
- **FR-P6** `--data-dir <folder>` keeps all settings and state in that folder (portable use).

## 4. User interface

### 4.1 Layout

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [orders-20261113.txt ×] [api-20261113.txt ×] [+]                     ─ □ ×   │
├──────────────────────────────────────────────────────────────────────────────┤
│ File  View  Filter  Tools  Help                                               │
│ Query: [ @l >= 'Warning' and @m like '%payment%'               ]  [?]         │
│ Levels: [V] [D] [I 18,402] [W 311] [E 27] [F 1]   Time: [All ▾]  [Tail ▶]     │
├──────────────────────────────────────────────────────────────────────────────┤
│ ▁▁▂▁▁▃▁▁▁▁▇▂▁▁▁▁▁▁▁▂▅▁▁▁▁▁▁▁   (timeline, drag to select a range)            │
├───────────────────────────────────────────────────────────┬──────────────────┤
│ Time          Lvl  Message                                │ Message          │
│ 10:15:32.123  INF  Order 42 shipped                       │ Exception        │
│ 10:15:32.410  WRN  Retry 2/3 for payment gateway          │ Raw              │
│▌10:15:33.002  ERR  Payment failed for order 42            │                  │
│ 10:15:33.100  INF  Order 43 shipped                       │                  │
├───────────────────────────────────────────────────────────┴──────────────────┤
│ 18,741 of 18,741 events · 27 errors · Serilog text · UTF-8 · ● Live           │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 4.2 UX requirements

- **UX-1** Keyboard-first: Ctrl+O open, Ctrl+F find, F3 / Shift+F3, Ctrl+G go to, Ctrl+L query bar,
  Ctrl+T tail, Ctrl+W close tab, Esc clears, F1 shortcut list.
- **UX-2** Empty state on launch: drop zone, recent files and an *Open* button.
- **UX-3** Useful error states: file not found, access denied, "0 events parsed — check the format",
  query errors with their position.
- **UX-4** Large files load in the background with progress and cancel; the grid is usable as soon
  as the first rows are parsed.
- **UX-5** Accessibility: full keyboard navigation, UI Automation names, a screen-reader-friendly
  grid, 4.5:1 text contrast, level never conveyed by colour alone.
- **UX-6** Per-monitor DPI and high-contrast mode.
- **UX-7** Windows 11 look: Fluent controls, Mica (solid on Windows 10), tab strip in the title bar,
  `InfoBar` for non-blocking errors.

## 5. Non-functional requirements

| ID | Requirement | Target |
|---|---|---|
| NFR-1 | Cold start to interactive window | < 2 s on a mid-range laptop |
| NFR-2 | Open a 30 MB file (≈ 250 k events) | Fully parsed and browsable < 1 s |
| NFR-3 | Stress: 100 MB file | Fully parsed < 5 s; UI responsive while loading |
| NFR-4 | Level / text filter over 250 k events | < 300 ms |
| NFR-5 | Memory for a 30 MB file | < 250 MB working set |
| NFR-6 | Scrolling | Smooth with up to 1 M rows |
| NFR-7 | Live tail latency | New event visible ≤ 1 s after being flushed |
| NFR-8 | Robustness | Never crash on malformed input; parse errors contained per line |
| NFR-9 | Privacy | No telemetry and no network calls; logs never leave the machine |
| NFR-10 | Security | Read-only access; regex timeouts; JSON depth / size limits; no code execution from log content |
| NFR-11 | Installation | Portable self-contained single-file exe, no admin rights |
| NFR-12 | Diagnostics | The viewer's own log and crash reports in `%LOCALAPPDATA%\LogLens\logs` |

## 6. Technical design

### 6.1 Stack

| Concern | Choice |
|---|---|
| Language / runtime | C# on .NET 10 (LTS); SDK pinned in `global.json` |
| UI | WinUI 3 on the Windows App SDK 1.8 |
| Event list | Custom `LogGrid`: a pool of row visuals plus a `ScrollBar`; scroll and selection maths in Core (`GridViewport`, `SelectionModel`). Not `ListView`, whose scroll extent overflows above ~127 k rows at 150 % scaling |
| Pattern | MVVM with `CommunityToolkit.Mvvm`, DI with `Microsoft.Extensions.DependencyInjection` |
| Query language | Hand-written lexer / parser compiled to a predicate |
| Self-diagnostics | Serilog with the file sink |
| Tests | xUnit with plain `Assert` |
| Packaging | Unpackaged, self-contained, single-file exe (x64 and Arm64); signed MSIX + winget later |

### 6.2 Architecture

```
┌──────────────────────── LogLens.App (WinUI 3) ────────────────────────┐
│ MainWindow · tabs · LogGrid · detail pane · query bar · timeline       │
└───────────────▲───────────────────────────────────────────────────────┘
┌───────────────┴──────────── LogLens.ViewModels ───────────────────────┐
│ LogViewModel (filtering, search, tail, bookmarks, analysis, export)    │
└───────────────▲───────────────────────────────────────▲───────────────┘
┌───────────────┴──────────────┐   ┌────────────────────┴───────────────┐
│ LogLens.Core (no UI / OS)    │   │ LogLens.Infrastructure              │
│ LogEvent model · format      │   │ File loading and tailing · folder   │
│ parsers · query engine ·     │   │ scan · settings / state stores ·    │
│ filtering · analysis ·       │   │ diagnostics · single instance ·     │
│ export writers               │   │ Open with registration              │
└──────────────────────────────┘   └─────────────────────────────────────┘
```

Solution layout: `src/LogLens.Core`, `src/LogLens.Infrastructure`, `src/LogLens.ViewModels`,
`src/LogLens.App`, with test projects under `tests/` for the first three and sample logs under
`samples/`. WinUI code has no unit tests, so logic stays out of code-behind.

**Formats.** A format is a parser in Core that turns chunks of text into `LogEvent`s, can be
re-fed incrementally while tailing, and declares which fields it fills (properties, template,
source context). Loading, filtering and the UI only see the common model, so adding a format is
adding a parser and its tests.

### 6.3 File-size strategy

1. **Background load**: open with `FileShare.ReadWrite | Delete`, read in chunks on a worker, split
   into events and publish them to the UI in batches.
2. **Filtering and search** run over the in-memory list and produce an index array; the grid binds
   to a view over that array, so filtering never copies events.
3. **Tail** remembers the last byte offset, reads only new bytes, holds back an incomplete last
   line, and appends in coalesced batches.
4. Long work takes a `CancellationToken`; a new filter replaces the previous pass.
5. Designed for ~100 MB (NFR-3). An on-disk index is not built in v1.

### 6.4 Core data model

```csharp
record LogEvent(
    long Id,                      // sequential within the document
    string SourceFile,
    long Offset, int Length,      // raw text lives in a shared text store
    DateTimeOffset Timestamp,
    LogLevel Level,               // Verbose..Fatal, Unknown, Unparsed
    string Message,
    string? Exception,
    string? MessageTemplate,      // structured formats only
    IReadOnlyDictionary<string, LogValue>? Properties);
```

### 6.5 Storage locations

| Data | Location |
|---|---|
| Settings, saved filters, hide rules, column layout | `%APPDATA%\LogLens\settings.json` |
| Recent files, bookmarks, open tabs | `%APPDATA%\LogLens\state.json` |
| Diagnostics log and crash reports | `%LOCALAPPDATA%\LogLens\logs\` |

With `--data-dir <folder>` all of these live in `<folder>\LogLens`.

## 7. Testing & quality

- **Parser tests** against sample files: multi-line stack traces, abbreviated levels, timestamps with
  and without offset, Unicode, empty lines, an incomplete last line, very long messages.
- **Query tests**: grammar, precedence, type coercion, invalid queries.
- **Tail tests**: a concurrent writer, truncation, replacement, partial lines.
- **Performance tests**: synthetic 30 MB and 100 MB logs checked against NFR-2 … NFR-5.
- **Manual checks** per release: keyboard-only walkthrough, Narrator, light / dark, the published
  exe started from an empty folder.

## 8. Roadmap

Each release is usable on its own and is published on GitHub with a portable exe.

| Release | Scope | Outcome |
|---|---|---|
| **v0.1** – Read & show | Solution, DI, theming, format interface with the Serilog text parser (multi-line exceptions), file loading, virtual grid, detail pane (message, exception, raw), level colours, open by dialog / drag-and-drop / command line, single-file publish | Open any Serilog default-template log and browse it |
| **v0.2** – Find things | Level and time filters, find bar, query bar, quick filters, go to event / time, next / previous warning or error | Answer "what went wrong at 10:15?" |
| **v0.3** – Live logs | Background load with progress, tail, refresh, rolling files, single instance, recent files, reopen last session | Usable as a tail tool |
| **v0.4** – Many logs | Tabs in the title bar, open folder, merge view, bookmarks, find all containing | Follow a request across files |
| **v0.5** – Analyse & export | Timeline, statistics, export, relative time column, newest first, wrapped messages, column layout | Feature-complete for Serilog text |
| **v1.0** – Polish & ship | Settings dialog, saved filters, hide rules, settings export / import, Open with, `--data-dir`, crash reports, first-run hints, accessibility pass | First stable release |
| **v1.x** – Structured formats | CLEF and classic JSON with auto-detect and a per-tab override, properties pane and property filters, Context column, custom templates | Structured logs |
| **v1.x** – Other loggers | NLog, log4net, custom regex | Logs not written by Serilog |
| **v1.x** – Remote hosts | Open logs over SSH / SFTP and Windows shares; remote tail | Logs on servers without RDP |

## 9. Future ideas

- `.zip` / `.gz` archive support.
- Diff two time ranges or two files ("good run vs bad run").
- Pattern clustering of unstructured messages.
- *Open in Seq* link; copy an event as a GitHub issue.
- A cross-platform front end reusing the UI-free Core and Infrastructure.

## 10. v1 acceptance checklist

- [ ] Opens default-template Serilog files of 1–30 MB with zero dropped lines.
- [ ] Multi-line exceptions display as one event with a formatted stack trace.
- [ ] Filtering by level, time range and text (including regex) works and shows live counts.
- [ ] The query bar parses the documented grammar and reports errors inline.
- [ ] A 30 MB file is fully parsed in < 1 s; a 100 MB file stays responsive while loading.
- [ ] Tail follows a file written by another process, survives rotation, and never shows half lines.
- [ ] Merge view orders events from ≥ 3 files chronologically with a Source column.
- [ ] Export of the filtered set produces valid CSV / JSON / HTML.
- [ ] Light / dark themes, DPI scaling and keyboard-only operation verified.
- [ ] The portable exe runs on a clean Windows 10 1809+ and Windows 11 machine without admin rights.
- [ ] No network traffic and no file modified.
