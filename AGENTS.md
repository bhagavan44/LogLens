# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this is

LogLens is an offline, read-only Windows desktop log viewer. **Read [SPEC.md](SPEC.md) first** — it
is the source of truth for scope, requirement IDs (FR- / NFR- / UX-) and the release roadmap (§8).
[README.md](README.md) is the user guide and ships with releases. [CONTRIBUTING.md](CONTRIBUTING.md)
covers the contributor workflow.

v1 supports **Serilog's default plain-text template only**. Other formats come after v1.0, but the
code must stay ready for them (see *Formats* below).

## Stack

- .NET 10 (`global.json` pins the SDK), C#, nullable enabled. Shared properties and the version are
  in `Directory.Build.props`.
- UI: **WinUI 3** on the Windows App SDK 1.8, MVVM with `CommunityToolkit.Mvvm`, DI via
  `Microsoft.Extensions.DependencyInjection`.
- Unpackaged + self-contained: `pwsh build/publish.ps1` produces **one single-file exe**
  (`artifacts/LogLens-<version>-<runtime>.exe`), no installer, no runtime to install.
- Tests: xUnit with plain `Assert` (deliberately **not** FluentAssertions: v8+ is commercial).

## Layout

| Path | Purpose |
|---|---|
| `src/LogLens.Core` | UI-free models, format parsers, filtering and query language, analysis, export writers. No Windows or file-system dependencies. |
| `src/LogLens.Infrastructure` | File loading and tailing, settings / state stores, diagnostics, single instance, Open with registration. Depends on Core. |
| `src/LogLens.ViewModels` | UI-free view-models. No WinUI types, so they stay unit-testable. |
| `src/LogLens.App` | WinUI 3 app: views, services, composition root. Depends on all of the above. |
| `tests/*` | Unit tests for Core, Infrastructure and ViewModels. WinUI code has no unit tests. |
| `samples/` | Sample logs, one folder per format. |
| `build/` | Publish and asset scripts. |

## Commands

```bash
dotnet build LogLens.sln -c Release
dotnet test LogLens.sln -c Release
pwsh build/publish.ps1            # single-file exe in artifacts/
```

Before calling a task done, run the build and the tests. Run the publish script too if packaging,
XAML or the Windows App SDK changed, and **start the published exe under its artifact name** from
an otherwise empty folder.

**Run a build with `--data-dir <scratch folder>`** so it does not touch the real profile
(`%APPDATA%\LogLens`, `%LOCALAPPDATA%\LogLens`).

## Conventions

- **Layers:** Core has no UI / Windows APIs; Infrastructure depends on Core; ViewModels has no
  WinUI types; App depends on all three. Keep logic out of code-behind and test it where it lives.
- **Formats:** every format is a parser behind the format interface in Core, producing the common
  `LogEvent` model. Don't special-case a format in the grid, filters, statistics or export. Each
  parser gets its own tests and a `samples/<format>/` folder. Don't add a new format before its
  release in SPEC.md §8.
- **Don't use `ListView` for the event list.** Its scroll extent overflows at ~127 k rows (150 %
  scale). Use `LogGrid` and keep scroll / selection maths in Core (`GridViewport`, `SelectionModel`).
- **Every row has the same height**, also with wrapped messages. The viewport counts rows, not
  pixels; don't add per-row heights.
- Long work (parsing, filtering, tailing) runs off the UI thread with `CancellationToken`s and
  reaches the UI in batches. **Never do file I/O or parsing inside an `async IAsyncEnumerable`
  body that the UI thread iterates** — after each `yield` it resumes on the caller's thread.
- A filter pass must never be cancelled without applying a replacement: an invalid query keeps the
  last valid one.
- Never take an exclusive lock on a log file: open with `FileShare.ReadWrite | FileShare.Delete`.
  The viewer is read-only.
- No telemetry and no network calls (NFR-9).
- Malformed input never crashes the app: unparsable lines become *Unparsed* events (FR-F5).
- In dialogs, set brushes through styles with `{ThemeResource …}`, not from
  `Application.Current.Resources` in code.

## Git

- Work on a new branch, not on `main`. One round of the roadmap (SPEC.md §8) per branch / PR.
- Conventional commit messages (`feat:`, `fix:`, `chore:`), short and descriptive, one change per
  commit. They drive release-please, which bumps `Directory.Build.props` and `CHANGELOG.md`.
- Don't commit build output (`bin/`, `obj/`, `artifacts/`) or the local `.claude/` folder.
