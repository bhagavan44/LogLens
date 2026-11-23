using LogLens.Core.Settings;

namespace LogLens.Infrastructure.State;

/// <summary>Keeps <see cref="AppSettings"/> in <c>settings.json</c> (SPEC §6.5).</summary>
public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    private readonly JsonFileStore<AppSettings> _file = new(paths.SettingsFile, () => new AppSettings());

    public AppSettings Load()
    {
        // A hand-edited file may hold nulls where objects belong.
        var settings = _file.Load();
        return settings with { Columns = (settings.Columns ?? new ColumnLayout()).Normalised() };
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _file.Save(settings with { Version = AppSettings.CurrentVersion });
    }
}
