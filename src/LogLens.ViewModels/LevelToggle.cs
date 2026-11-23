using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>One level button of the filter bar: the level, how many events have it, and whether it is shown (FR-S1).</summary>
public sealed partial class LevelToggle : ObservableObject
{
    private readonly Action<LevelToggle> _changed;
    private bool _silent;

    public LevelToggle(LogLevel level, int count, Action<LevelToggle> changed)
    {
        Level = level;
        Count = count;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _silent = true;
        IsOn = true;
        _silent = false;
    }

    public LogLevel Level { get; }

    /// <summary>Number of events with this level; grows while the log loads or is tailed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(Description))]
    public partial int Count { get; internal set; }

    /// <summary>Short text on the button; never colour alone (UX-5).</summary>
    public string Label => Level == LogLevel.Unparsed ? "Unparsed" : LogText.LevelAbbreviation(Level);

    public string CountText => Count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Tooltip and screen-reader name.</summary>
    public string Description => $"{LogText.LevelName(Level)}: {CountText} {(Count == 1 ? "event" : "events")}. Click to show or hide.";

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_silent)
        {
            _changed(this);
        }
    }

    /// <summary>Sets the state without telling the view-model (used when several toggles change at once).</summary>
    internal void SetSilently(bool on)
    {
        _silent = true;
        try
        {
            IsOn = on;
        }
        finally
        {
            _silent = false;
        }
    }
}
