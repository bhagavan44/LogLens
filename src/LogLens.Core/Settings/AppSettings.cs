namespace LogLens.Core.Settings;

/// <summary>
/// Which columns of the log grid are shown, and the widths the user dragged them to (FR-G1). A null width means "automatic".
/// </summary>
public sealed record ColumnLayout
{
    /// <summary>Narrowest and widest a column can be dragged.</summary>
    public const double MinWidth = 40;

    public const double MaxWidth = 600;

    public bool ShowTime { get; init; } = true;

    public bool ShowLevel { get; init; } = true;

    /// <summary>Show the Source (file) column in merged views; other views never have it.</summary>
    public bool ShowSource { get; init; } = true;

    /// <summary>Width of the Time column; null fits the timestamp.</summary>
    public double? TimeWidth { get; init; }

    /// <summary>Width of the Source column; null uses the default.</summary>
    public double? SourceWidth { get; init; }

    /// <summary>The layout with widths rounded and kept between <see cref="MinWidth"/> and <see cref="MaxWidth"/>; unusable widths become automatic.</summary>
    public ColumnLayout Normalised() => this with { TimeWidth = Clamp(TimeWidth), SourceWidth = Clamp(SourceWidth) };

    private static double? Clamp(double? width)
        => width is { } w && double.IsFinite(w) ? Math.Clamp(Math.Round(w), MinWidth, MaxWidth) : null;
}

/// <summary>
/// User settings (<c>settings.json</c>, SPEC §6.5). This round holds the view options of the grid; the settings dialog, hide rules, saved
/// filters and the rest of FR-P1 … FR-P4 join it in v1.0.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>List the newest event first in every tab (View ▸ Newest events first, FR-G3).</summary>
    public bool NewestFirst { get; init; }

    /// <summary>Show a long message on up to two lines instead of one line cut off with an ellipsis (View ▸ Wrap long messages, FR-G6).</summary>
    public bool WrapMessages { get; init; }

    /// <summary>Show the timeline above the grid (View ▸ Timeline, FR-N2).</summary>
    public bool ShowTimeline { get; init; }

    /// <summary>Shown columns and their widths in the log grid (FR-G1).</summary>
    public ColumnLayout Columns { get; init; } = new();
}

/// <summary>Reads and writes <see cref="AppSettings"/>. Implemented in Infrastructure (a JSON file).</summary>
public interface ISettingsStore
{
    /// <summary>Loads the settings. Never throws: a missing or unreadable file gives the defaults.</summary>
    AppSettings Load();

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    void Save(AppSettings settings);
}

/// <summary>An <see cref="ISettingsStore"/> that keeps the settings in memory only: nothing outlives the process.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));
}

/// <summary>The settings in force: read once, changed through <see cref="Update"/>, saved on every change.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;

    public SettingsService(ISettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var loaded = store.Load();
        Current = loaded with { Columns = (loaded.Columns ?? new ColumnLayout()).Normalised() };
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised after the settings changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Applies <paramref name="change"/> and saves. Changing nothing neither saves nor raises <see cref="Changed"/>. A settings file that
    /// can't be written never costs the user the change itself: it stays in force until the app closes.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current);
        next = next with { Columns = (next.Columns ?? new ColumnLayout()).Normalised() };
        if (next == Current)
        {
            return;
        }

        Current = next;
        try
        {
            _store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory for this run
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
