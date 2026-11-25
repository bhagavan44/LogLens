# Contributing to LogLens

Thanks for taking the time to help. Bug reports, ideas, documentation fixes and code are all
welcome.

LogLens is an offline, read-only log viewer: it never changes the files it opens, sends no
telemetry and makes no network calls except the read-only SSH/SFTP connections the user starts to
hosts they chose (NFR-9). Contributions need to keep it that way.

## Ways to contribute

- **Report a bug**: open an issue with the *Bug report* form. A few log lines that show the problem
  (with anything private removed) and the steps to reproduce it help most.
- **Suggest a feature**: open an issue with the *Feature request* form and describe the problem
  you want solved, not only the solution.
- **Ask for a log format**: use the *Feature request* form and include a few sample lines and, if
  you know it, the logger configuration that wrote them.
- **Report a security problem**: don't open a public issue. See [SECURITY.md](SECURITY.md).
- **Send a pull request**: for anything bigger than a small fix, open an issue first so we can agree
  on the approach before you spend time on it.

## Getting started

You need Windows 10 (1809 or later) or Windows 11, and the .NET 10 SDK (the exact version is pinned
in `global.json`). Visual Studio 2026 with the *Windows application development* workload, or VS
Code with the C# Dev Kit, both work.

```bash
git clone https://github.com/<your-username>/LogLens.git
cd LogLens
dotnet build LogLens.sln -c Release
```

| Command | What it does |
|---|---|
| `dotnet build LogLens.sln -c Release` | Builds every project |
| `dotnet test LogLens.sln -c Release` | Runs the unit tests, as CI does |
| `pwsh build/publish.ps1` | Produces the portable single-file exe in `artifacts/` |

When you run a build you made, start it with `--data-dir <scratch folder>` so it keeps its
settings and state there instead of in your real profile.

[SPEC.md](SPEC.md) is the source of truth for scope, requirement IDs and the roadmap, and
[AGENTS.md](AGENTS.md) lists the conventions and the invariants that are easy to break. Read both
before changing code.

## Making a change

1. Fork the repository and create a branch from `main`, for example `fix/tail-truncation` or
   `feat/clef-parser`.
2. Make your change, keeping it focused on one thing.
3. Add or update tests (see below).
4. Run the checks:
   ```bash
   dotnet build LogLens.sln -c Release
   dotnet test LogLens.sln -c Release
   pwsh build/publish.ps1   # if packaging, XAML or the Windows App SDK changed
   ```
5. Commit using [Conventional Commits](https://www.conventionalcommits.org/) and open a pull
   request against `main`. The pull request template has a short checklist.

### Commit messages

Use a conventional prefix and a short subject in the imperative mood:

```
feat: add a query bar to the filter strip
fix: keep the selection when a filter is cleared
docs: explain the query syntax in the README
chore: bump Microsoft.WindowsAppSDK
```

Common prefixes are `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `ci` and `perf`. Keep each
commit to one change. The prefixes also set the next version (see [Releases](#releases)), so mark
breaking changes with `!` (`feat!: ...`) or a `BREAKING CHANGE:` footer.

## Code conventions

- **Layers.** `LogLens.Core` has no UI, Windows or file-system dependencies. `LogLens.Infrastructure`
  depends on Core. `LogLens.ViewModels` has no WinUI types. `LogLens.App` is the WinUI 3 shell and
  depends on all three.
- **Logic stays out of code-behind.** Parsing, filtering and the query language go in Core, file
  loading in Infrastructure, view state in ViewModels, so they can be unit-tested.
- **Log formats are pluggable.** A new format is a new parser behind the format interface in Core,
  with its own tests and sample files. Don't special-case a format in the grid, filters or export.
- C# with nullable reference types enabled. Formatting follows `.editorconfig` (4 spaces,
  file-scoped namespaces, `_camelCase` private fields). Don't reformat code you aren't changing.
- Don't add new dependencies without discussing it in an issue first.

### Things that are easy to break

- **Log files are only read.** Open them with `FileShare.ReadWrite | FileShare.Delete` and never
  take an exclusive lock, so the app writing the log is never blocked.
- **Every row has the same height.** The event list is virtualised and counts rows, not pixels.
- **Long work runs off the UI thread** with a `CancellationToken`, and results reach the UI in
  batches. Never block the UI thread on file I/O or parsing.
- **Malformed input never crashes the app.** A line that can't be parsed is kept as an *Unparsed*
  event, never dropped.

## Tests

- Unit tests use xUnit with plain `Assert`, under `tests/`. Every change to Core, Infrastructure or
  ViewModels needs a new or updated test.
- Parser changes need sample lines covering the edge cases: multi-line exceptions, missing or odd
  timestamps, unknown levels, an incomplete last line.
- WinUI code has no unit tests. For UI changes, check the result in the running app in both light
  and dark themes, and include a screenshot in the pull request.

## Pull request review

- CI builds the solution, runs the unit tests and publishes the exe on every pull request. All of
  them must pass.
- Keep pull requests small and focused; they get reviewed faster.
- Be ready to make changes after review. Once approved, the pull request is squash-merged into
  `main`.

## Releases

Releases are automated with [release-please](https://github.com/googleapis/release-please). It
reads the conventional commits on `main` and keeps a release pull request open that bumps the
version and updates [CHANGELOG.md](CHANGELOG.md). Merging it tags the release, and the portable
exe is built and attached to the GitHub release.

## License

By contributing, you agree that your contributions are licensed under the
[GNU General Public License v3.0](LICENSE), the same license as the project.

## Code of conduct

This project follows a [Code of Conduct](CODE_OF_CONDUCT.md). By taking part, you agree to uphold
it.
