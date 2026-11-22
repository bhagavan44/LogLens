## Summary

<!-- What does this change and why? -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor (no behaviour change)
- [ ] Documentation
- [ ] Build, CI or dependencies

## How it was tested

<!-- Steps you followed, a sample log (with anything private removed), or the tests you added. -->

## Screenshots

<!-- For UI changes: before and after, in light and dark themes. Delete if not relevant. -->

## Checklist

- [ ] The pull request title follows [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:` …)
- [ ] Unit tests pass (`dotnet test LogLens.sln -c Release`), with new or updated tests for every change to Core, Infrastructure or ViewModels
- [ ] The Release build succeeds (`dotnet build LogLens.sln -c Release`)
- [ ] The published exe starts (`pwsh build/publish.ps1`), if packaging, XAML or the Windows App SDK changed
- [ ] The README is updated (if a feature, shortcut or limit changed)
- [ ] Log files are only read, never changed, and nothing is sent over the network
