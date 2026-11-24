using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Filtering;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.ViewModels;

/// <summary>
/// What a tab takes from the settings: how timestamps are shown (FR-G5, FR-P3), the hide rules (FR-S6) and the saved filters (FR-S7). The
/// settings are shared by every tab; the window hands them to each one.
/// </summary>
public sealed partial class LogViewModel
{
    private IReadOnlyList<HideRule> _hideRules = [];

    // ------------------------------------------------------------------ timestamps (FR-G5, FR-P3)

    /// <summary>The zone and format timestamps are shown in. The time filter, the query bar, the timeline and Go to read times in the same zone.</summary>
    public TimeDisplay TimeDisplay { get; private set; } = TimeDisplay.Default;

    private TimestampZone Zone => TimeDisplay.Zone;

    /// <summary>Shows timestamps in another zone or format; filters on time are read in the new zone from now on.</summary>
    public void SetTimeDisplay(TimeDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (display == TimeDisplay)
        {
            return;
        }

        var zoneChanged = display.Zone != TimeDisplay.Zone;
        TimeDisplay = display;
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(DetailHeader));
        OnPropertyChanged(nameof(StatisticsSummary));
        RefreshBookmarkItems();

        // The grid re-reads its rows after a pass; a new zone also changes what @t, the time window and the timeline mean.
        QueueApply();
        if (!zoneChanged)
        {
            return;
        }

        if (_timeRange.IsOpen && string.IsNullOrWhiteSpace(TimeFromText) && string.IsNullOrWhiteSpace(TimeToText))
        {
            return;
        }

        // The window was typed (or picked) as wall-clock times in the old zone: keep its text and read it in the new one.
        ApplyTimeBoxes();
    }

    // ------------------------------------------------------------------ hide rules (FR-S6)

    /// <summary>Whether this tab applies the hide rules. Switched off, the hidden events show for a moment; not remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideRulesLabel))]
    public partial bool HideRulesOn { get; set; } = true;

    /// <summary>The rules that are switched on and can be read; a rule whose query has a mistake is ignored.</summary>
    public IReadOnlyList<HideRule> UsableHideRules => [.. _hideRules.Where(r => r.Enabled && QueryParser.TryParse(r.Query, Zone, out _, out _))];

    /// <summary>At least one hide rule applies: the filter bar shows the bypass button.</summary>
    public bool HasHideRules => UsableHideRules.Count > 0;

    /// <summary>"Hiding noise (2 rules)" / "Noise shown (2 rules off)".</summary>
    public string HideRulesLabel
    {
        get
        {
            var n = UsableHideRules.Count;
            var rules = n == 1 ? "1 rule" : $"{n} rules";
            return HideRulesOn ? $"Hiding noise ({rules})" : $"Noise shown ({rules} off)";
        }
    }

    /// <summary>The rules in force, one per line (the bypass button's tooltip).</summary>
    public string HideRulesList => string.Join("\n", UsableHideRules.Select(r => r.Label));

    /// <summary>Replaces the hide rules (the settings changed) and filters again when they differ.</summary>
    public void SetHideRules(IReadOnlyList<HideRule>? rules)
    {
        rules ??= [];
        if (rules.SequenceEqual(_hideRules))
        {
            return;
        }

        _hideRules = [.. rules];
        OnPropertyChanged(nameof(UsableHideRules));
        OnPropertyChanged(nameof(HasHideRules));
        OnPropertyChanged(nameof(HideRulesLabel));
        OnPropertyChanged(nameof(HideRulesList));
        QueueApply();
    }

    partial void OnHideRulesOnChanged(bool value) => QueueApply();

    /// <summary>The rules to apply in this pass; compiled afresh each time because a rule on <c>@t</c> depends on the zone.</summary>
    private List<CompiledQuery> CompileHideRules()
    {
        var compiled = new List<CompiledQuery>();
        if (!HideRulesOn)
        {
            return compiled;
        }

        foreach (var rule in _hideRules)
        {
            if (rule.Enabled && QueryParser.TryParse(rule.Query, Zone, out var query, out _) && query is not null)
            {
                compiled.Add(query);
            }
        }

        return compiled;
    }

    // ------------------------------------------------------------------ saved filters (FR-S7)

    /// <summary>The query text and the level buttons as they are now, under <paramref name="name"/>.</summary>
    public SavedFilter CaptureFilter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new SavedFilter { Name = name.Trim(), Query = QueryText.Trim(), Levels = CurrentLevelMask() };
    }

    /// <summary>True when the query and the level buttons are set as <paramref name="filter"/> has them.</summary>
    public bool Matches(SavedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return string.Equals(QueryText.Trim(), filter.Query, StringComparison.Ordinal) && CurrentLevelMask() == LevelsShownBy(filter.Levels);
    }

    /// <summary>Sets the level buttons and the query bar from <paramref name="filter"/>, with a single filter pass. Other filters stay as they are.</summary>
    public void ApplySavedFilter(SavedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _suspendApply = true;
        try
        {
            foreach (var toggle in LevelToggles)
            {
                toggle.SetSilently(LevelMask.Allows(filter.Levels, toggle.Level));
            }

            SyncPresetFromToggles();
            QueryText = filter.Query;
        }
        finally
        {
            _suspendApply = false;
        }

        QueueApply();
    }

    /// <summary>The mask the level buttons of this log end up with for <paramref name="levels"/>: levels without a button are always shown.</summary>
    private int LevelsShownBy(int levels)
    {
        var mask = LevelMask.All;
        foreach (var t in LevelToggles)
        {
            mask = LevelMask.With(mask, t.Level, LevelMask.Allows(levels, t.Level));
        }

        return mask;
    }
}
