using System.Text.Json.Serialization;
using LogLens.Core.Formats;
using LogLens.Core.Models;

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

    /// <summary>Show the Context column (the <c>SourceContext</c> property) for logs whose format has one; other logs never have it.</summary>
    public bool ShowContext { get; init; } = true;

    /// <summary>Width of the Context column; null uses the default.</summary>
    public double? ContextWidth { get; init; }

    /// <summary>The layout with widths rounded and kept between <see cref="MinWidth"/> and <see cref="MaxWidth"/>; unusable widths become automatic.</summary>
    public ColumnLayout Normalised() => this with { TimeWidth = Clamp(TimeWidth), SourceWidth = Clamp(SourceWidth), ContextWidth = Clamp(ContextWidth) };

    private static double? Clamp(double? width)
        => width is { } w && double.IsFinite(w) ? Math.Clamp(Math.Round(w), MinWidth, MaxWidth) : null;
}

/// <summary>The app's colours (FR-P1).</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows setting.</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>How tall the rows of the log grid are (FR-P2).</summary>
public enum RowDensity
{
    Comfortable = 0,
    Compact = 1,
}

/// <summary>A noise filter (FR-S6): while <see cref="Enabled"/>, events matching <see cref="Query"/> (query bar syntax) are left out of every log.</summary>
public sealed record HideRule
{
    public string Name { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>What a list shows for the rule: its name, or its query when it has none.</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Query : Name;
}

/// <summary>A named filter (FR-S7): the query text and the levels that were switched on.</summary>
public sealed record SavedFilter
{
    public string Name { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    /// <summary>Bit mask of the levels shown (see <see cref="Filtering.LevelMask"/>); all levels when not set.</summary>
    public int Levels { get; init; } = Filtering.LevelMask.All;
}

/// <summary>
/// User settings (<c>settings.json</c>, SPEC §6.5): appearance (FR-P1, FR-P2), timestamps and start-up (FR-P3), hide rules and saved
/// filters (FR-P4), custom output templates (FR-F2), and the view options of the grid. The column layout and <see cref="HintsSeen"/> belong to this PC and are left out of
/// an exported settings file.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    /// <summary>The log font unless the user picks another. Inter ships inside the app (SIL OFL), so it is always there.</summary>
    public const string DefaultFontFamily = "Inter";

    public const double DefaultFontSize = 12;

    /// <summary>Smallest and largest font sizes the settings accept.</summary>
    public const double MinFontSize = 9;

    public const double MaxFontSize = 20;

    /// <summary>More hide rules or saved filters than this are dropped when the settings are read: they are a mistake, not a list.</summary>
    public const int MaxListLength = 100;

    public int Version { get; init; } = CurrentVersion;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>Font of the log text in the grid and the message pane (FR-P2).</summary>
    public string FontFamily { get; init; } = DefaultFontFamily;

    public double FontSize { get; init; } = DefaultFontSize;

    public RowDensity RowDensity { get; init; } = RowDensity.Comfortable;

    /// <summary>The clock timestamps are shown in (FR-G5, FR-P3).</summary>
    public TimestampZone TimestampZone { get; init; } = TimestampZone.AsLogged;

    /// <summary>Format of the Time column: a .NET custom date and time format (FR-G5, FR-P3).</summary>
    public string TimestampFormat { get; init; } = TimeDisplay.DefaultFormat;

    /// <summary>Reopen the tabs that were open when LogLens last closed (FR-P3).</summary>
    public bool RestoreSession { get; init; } = true;

    /// <summary>Start following new events as soon as a file opens (FR-P3).</summary>
    public bool TailByDefault { get; init; }

    /// <summary>List the newest event first in every tab (View ▸ Newest events first, FR-G3).</summary>
    public bool NewestFirst { get; init; }

    /// <summary>Show a long message on up to two lines instead of one line cut off with an ellipsis (View ▸ Wrap long messages, FR-G6).</summary>
    public bool WrapMessages { get; init; }

    /// <summary>Show the timeline above the grid (View ▸ Timeline, FR-N2).</summary>
    public bool ShowTimeline { get; init; }

    /// <summary>Shown columns and their widths in the log grid (FR-G1). Belongs to this PC: not exported.</summary>
    public ColumnLayout Columns { get; init; } = new();

    /// <summary>The first-run hints (UX-7) have been shown. Belongs to this PC: not exported, and an import keeps the current value.</summary>
    public bool HintsSeen { get; init; }

    /// <summary>Hide rules applied to every log (FR-S6).</summary>
    public IReadOnlyList<HideRule> HideRules { get; init; } = [];

    /// <summary>Named filters that can be recalled from the filter bar (FR-S7).</summary>
    public IReadOnlyList<SavedFilter> SavedFilters { get; init; } = [];

    /// <summary>The user's own Serilog output templates (FR-F2), offered next to the built-in formats and tried by detection (FR-F1).</summary>
    public IReadOnlyList<CustomTemplate> CustomTemplates { get; init; } = [];

    /// <summary>How timestamps are shown with these settings.</summary>
    public TimeDisplay ToTimeDisplay() => new(TimestampZone, TimestampFormat);

    /// <summary>The formats files can be read with: the built-in ones and <see cref="CustomTemplates"/>.</summary>
    public FormatCatalog ToFormatCatalog() => CustomTemplates.Count == 0 ? FormatCatalog.BuiltIn : new FormatCatalog(CustomTemplates);

    /// <summary>
    /// The settings with every value a hand-edited or imported file could make unusable put right: sizes clamped, unknown enum values and
    /// unusable formats back to their defaults, nulls replaced, empty rules and filters dropped. Always safe to call again.
    /// </summary>
    public AppSettings Normalised() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily.Trim(),
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(Math.Round(FontSize, 1), MinFontSize, MaxFontSize) : DefaultFontSize,
        RowDensity = Enum.IsDefined(RowDensity) ? RowDensity : RowDensity.Comfortable,
        TimestampZone = Enum.IsDefined(TimestampZone) ? TimestampZone : TimestampZone.AsLogged,
        TimestampFormat = TimeDisplay.IsUsableFormat(TimestampFormat) ? TimestampFormat : TimeDisplay.DefaultFormat,
        Columns = (Columns ?? new ColumnLayout()).Normalised(),
        HideRules = (HideRules ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Query))
            .Select(r => r with { Name = r.Name?.Trim() ?? string.Empty, Query = r.Query.Trim() })
            .Take(MaxListLength)
            .ToList(),
        SavedFilters = (SavedFilters ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => f with { Name = f.Name.Trim(), Query = f.Query?.Trim() ?? string.Empty })
            .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListLength)
            .ToList(),
        CustomTemplates = (CustomTemplates ?? [])
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.Template))
            .Select(t => new CustomTemplate(t.Name.Trim(), t.Template.Trim()))
            .DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListLength)
            .ToList(),
    };

    /// <summary>These settings with this PC's own part (column layout, first-run hints) taken from <paramref name="other"/>.</summary>
    public AppSettings WithMachineStateOf(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return this with { Columns = other.Columns, HintsSeen = other.HintsSeen };
    }

    /// <summary>These settings without this PC's own part, for an exported file.</summary>
    public AppSettings Portable() => this with { Version = CurrentVersion, Columns = new ColumnLayout(), HintsSeen = false };

    // A record compares lists by reference; settings compare them by value, so "nothing changed" is noticed after a load or an edit.
    // Keep this in step with the properties above (SettingsModelTests changes each property in turn).
    public bool Equals(AppSettings? other)
        => other is not null
           && Version == other.Version
           && Theme == other.Theme
           && FontFamily == other.FontFamily
           && FontSize.Equals(other.FontSize)
           && RowDensity == other.RowDensity
           && TimestampZone == other.TimestampZone
           && TimestampFormat == other.TimestampFormat
           && RestoreSession == other.RestoreSession
           && TailByDefault == other.TailByDefault
           && NewestFirst == other.NewestFirst
           && WrapMessages == other.WrapMessages
           && ShowTimeline == other.ShowTimeline
           && Equals(Columns, other.Columns)
           && HintsSeen == other.HintsSeen
           && SequenceEqual(HideRules, other.HideRules)
           && SequenceEqual(SavedFilters, other.SavedFilters)
           && SequenceEqual(CustomTemplates, other.CustomTemplates);

    public override int GetHashCode()
        => HashCode.Combine(Theme, FontFamily, FontSize, TimestampZone, TimestampFormat, NewestFirst, HideRules?.Count, SavedFilters?.Count);

    private static bool SequenceEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
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
        Current = (store.Load() ?? new AppSettings()).Normalised();
    }

    public AppSettings Current { get; private set; }

    /// <summary>
    /// The formats files can be read with under the current settings (built-in plus custom templates). The same instance is returned until the
    /// custom templates change, so a format chosen for a tab stays the same object across unrelated setting changes.
    /// </summary>
    public FormatCatalog Formats
    {
        get
        {
            var templates = Current.CustomTemplates;
            if (_formats is not { } cached || !cached.Templates.SequenceEqual(templates))
            {
                cached = (templates, Current.ToFormatCatalog());
                _formats = cached;
            }

            return cached.Catalog;
        }
    }

    private (IReadOnlyList<CustomTemplate> Templates, FormatCatalog Catalog)? _formats;

    /// <summary>Raised after the settings changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Applies <paramref name="change"/> and saves. Changing nothing neither saves nor raises <see cref="Changed"/>. A settings file that
    /// can't be written never costs the user the change itself: it stays in force until the app closes.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = (change(Current) ?? Current).Normalised();
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
