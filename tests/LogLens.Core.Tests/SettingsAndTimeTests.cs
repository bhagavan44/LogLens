using System.Reflection;
using LogLens.Core.Analysis;
using LogLens.Core.Collections;
using LogLens.Core.Filtering;
using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.Settings;

namespace LogLens.Core.Tests;

/// <summary>Timestamp zone and format (FR-G5, FR-P3): what the grid shows and what the time filters compare against.</summary>
public class TimeDisplayTests
{
    // 08:00:05.301 at +05:30 is 02:30:05.301 UTC.
    private static readonly DateTimeOffset At = new(2026, 11, 13, 8, 0, 5, 301, TimeSpan.FromMinutes(330));

    private static LogEvent Event(DateTimeOffset at, int id = 0) => new(id, null, 0, 0, at, LogLevel.Information, "m", null);

    [Fact]
    public void The_default_shows_the_time_as_logged()
    {
        Assert.Equal("2026-11-13 08:00:05.301", TimeDisplay.Default.Time(Event(At)));
        Assert.Equal("2026-11-13 08:00:05.301 +05:30", TimeDisplay.Default.FullTime(Event(At)));
        Assert.Equal(TimeDisplay.Default.Time(At), LogText.Time(At));
    }

    [Fact]
    public void Utc_converts_the_clock_and_the_offset()
    {
        var utc = new TimeDisplay(TimestampZone.Utc);

        Assert.Equal("2026-11-13 02:30:05.301", utc.Time(Event(At)));
        Assert.Equal("2026-11-13 02:30:05.301 +00:00", utc.FullTime(At));
        Assert.Equal(new DateTime(2026, 11, 13, 2, 30, 5, 301), utc.ToWall(At));
    }

    [Fact]
    public void Local_uses_this_pcs_time_zone()
    {
        var local = new TimeDisplay(TimestampZone.Local);

        Assert.Equal(At.ToLocalTime().DateTime, local.ToWall(At));
        Assert.Equal(At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), local.Time(At));
    }

    [Fact]
    public void A_custom_format_is_used_for_the_time_column_but_not_the_full_time()
    {
        var display = new TimeDisplay(TimestampZone.AsLogged, "HH:mm:ss.fff");

        Assert.Equal("08:00:05.301", display.Time(Event(At)));
        Assert.Equal("2026-11-13 08:00:05.301 +05:30", display.FullTime(At));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%")] // throws FormatException
    public void An_unusable_format_falls_back_to_the_default(string? format)
    {
        Assert.False(TimeDisplay.IsUsableFormat(format));
        Assert.Equal(TimeDisplay.DefaultFormat, new TimeDisplay(TimestampZone.Utc, format).Format);
    }

    [Fact]
    public void A_very_long_format_is_refused()
    {
        Assert.False(TimeDisplay.IsUsableFormat(new string('y', TimeDisplay.MaxFormatLength + 1)));
        Assert.True(TimeDisplay.IsUsableFormat("yyyy"));
    }

    [Fact]
    public void An_unknown_zone_is_read_as_logged() => Assert.Equal(TimestampZone.AsLogged, new TimeDisplay((TimestampZone)42).Zone);

    [Fact]
    public void Relative_cells_do_not_depend_on_the_zone()
    {
        var a = Event(At);
        var b = Event(At.AddSeconds(2), 1);

        Assert.Equal("+2.000 s", new TimeDisplay(TimestampZone.Utc).TimeCell(b, a, TimeColumnMode.SincePrevious));
        Assert.Equal("2026-11-13 02:30:07.301", new TimeDisplay(TimestampZone.Utc).TimeCell(b, a, TimeColumnMode.Timestamp));
        Assert.Equal("—", new TimeDisplay(TimestampZone.Utc).Time(new LogEvent(2, null, 0, 0, default, LogLevel.Unparsed, "x", null)));
    }

    [Fact]
    public void The_wall_clock_follows_the_zone()
    {
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 5, 301), TimeRange.WallClock(At));
        Assert.Equal(new DateTime(2026, 11, 13, 2, 30, 5, 301), TimeRange.WallClock(At, TimestampZone.Utc));
        Assert.Equal(At.ToLocalTime().DateTime, TimeRange.WallClock(At, TimestampZone.Local));
    }

    [Fact]
    public void The_time_window_is_read_in_the_zone_shown()
    {
        var e = Event(At);
        var utcWindow = new TimeRange(new DateTime(2026, 11, 13, 2, 30, 0), new DateTime(2026, 11, 13, 2, 31, 0));

        Assert.False(new FilterSpec { Time = utcWindow }.BuildPredicate()!(e));
        Assert.True(new FilterSpec { Time = utcWindow, Zone = TimestampZone.Utc }.BuildPredicate()!(e));
    }

    [Fact]
    public void A_query_on_the_time_compares_with_the_clock_shown()
    {
        var e = Event(At);

        Assert.True(QueryParser.TryParse("@t >= '2026-11-13 08:00'", out var asLogged, out _));
        Assert.True(QueryParser.TryParse("@t >= '2026-11-13 08:00'", TimestampZone.Utc, out var inUtc, out _));
        Assert.True(QueryParser.TryParse("@t < '2026-11-13 02:31'", TimestampZone.Utc, out var beforeUtc, out _));

        Assert.True(asLogged!.Predicate(e));
        Assert.False(inUtc!.Predicate(e));
        Assert.True(beforeUtc!.Predicate(e));
    }

    [Fact]
    public void A_time_with_an_offset_is_an_instant_in_every_zone()
    {
        var e = Event(At);
        Assert.True(QueryParser.TryParse("@t = '2026-11-13T02:30:05.301Z'", TimestampZone.Local, out var query, out _));
        Assert.True(query!.Predicate(e));
    }

    [Fact]
    public void Go_to_time_reads_the_time_in_the_zone_shown()
    {
        var rows = new[] { Event(At), Event(At.AddHours(1), 1) };
        Assert.True(TimeLiteral.TryParse("2026-11-13 03:00", null, out var target));

        Assert.Equal(0, GoTo.IndexAtOrAfter(rows, target)); // 03:00 as logged is before both events
        Assert.Equal(1, GoTo.IndexAtOrAfter(rows, target, TimestampZone.Utc)); // 03:00 UTC is after the first (02:30 UTC)
    }

    [Fact]
    public void The_timeline_spans_the_wall_clock_shown()
    {
        var rows = new[] { Event(At), Event(At.AddMinutes(10), 1) };

        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 5, 301), TimelineBuilder.Build(rows, null).Start);
        Assert.Equal(new DateTime(2026, 11, 13, 2, 30, 5, 301), TimelineBuilder.Build(rows, null, zone: TimestampZone.Utc).Start);
    }
}

/// <summary>Hide rules in the filter (FR-S6).</summary>
public class HideRuleFilterTests
{
    private static LogEvent Event(string message) => new(0, null, 0, 0, DateTimeOffset.UnixEpoch, LogLevel.Information, message, null);

    [Fact]
    public void An_event_matching_a_hide_rule_is_left_out()
    {
        Assert.True(QueryParser.TryParse("@m startswith 'Health'", out var rule, out _));
        var spec = new FilterSpec { Hide = [rule!] };

        Assert.True(spec.IsActive);
        var keep = spec.BuildPredicate()!;
        Assert.False(keep(Event("Health check OK")));
        Assert.True(keep(Event("Order shipped")));
    }

    [Fact]
    public void Without_rules_nothing_is_filtered() => Assert.Null(new FilterSpec { Hide = [] }.BuildPredicate());
}

/// <summary>The settings model (FR-P1 … FR-P4): defaults, putting right what a hand-edited file breaks, value equality and what travels to another PC.</summary>
public class SettingsModelTests
{
    [Fact]
    public void The_defaults_match_the_spec()
    {
        var s = new AppSettings();

        Assert.Equal(ThemePreference.System, s.Theme);
        Assert.Equal("Inter", s.FontFamily);
        Assert.Equal(12, s.FontSize);
        Assert.Equal(RowDensity.Comfortable, s.RowDensity);
        Assert.Equal(TimestampZone.AsLogged, s.TimestampZone);
        Assert.Equal(TimeDisplay.DefaultFormat, s.TimestampFormat);
        Assert.True(s.RestoreSession);
        Assert.False(s.TailByDefault);
        Assert.Empty(s.HideRules);
        Assert.Empty(s.SavedFilters);
        Assert.Equal(s, s.Normalised());
    }

    [Fact]
    public void Normalising_puts_right_what_a_hand_edited_file_can_break()
    {
        var broken = new AppSettings
        {
            Theme = (ThemePreference)9,
            FontFamily = "  ",
            FontSize = 400,
            RowDensity = (RowDensity)7,
            TimestampZone = (TimestampZone)5,
            TimestampFormat = "%",
            Columns = null!,
            HideRules = [null!, new HideRule { Query = " " }, new HideRule { Name = " Noise ", Query = " @l = 'Verbose' " }],
            SavedFilters = [new SavedFilter { Name = "" }, new SavedFilter { Name = " Errors ", Query = null! }, new SavedFilter { Name = "errors" }],
        };

        var s = broken.Normalised();

        Assert.Equal(ThemePreference.System, s.Theme);
        Assert.Equal(AppSettings.DefaultFontFamily, s.FontFamily);
        Assert.Equal(AppSettings.MaxFontSize, s.FontSize);
        Assert.Equal(RowDensity.Comfortable, s.RowDensity);
        Assert.Equal(TimestampZone.AsLogged, s.TimestampZone);
        Assert.Equal(TimeDisplay.DefaultFormat, s.TimestampFormat);
        Assert.Equal(new ColumnLayout(), s.Columns);
        Assert.Equal([new HideRule { Name = "Noise", Query = "@l = 'Verbose'" }], s.HideRules);
        Assert.Equal([new SavedFilter { Name = "Errors", Query = string.Empty }], s.SavedFilters); // names are unique, ignoring case
        Assert.Equal(AppSettings.MinFontSize, (broken with { FontSize = 1 }).Normalised().FontSize);
        Assert.Equal(AppSettings.DefaultFontSize, (broken with { FontSize = double.NaN }).Normalised().FontSize);
    }

    [Fact]
    public void Long_lists_are_cut()
    {
        var many = Enumerable.Range(0, AppSettings.MaxListLength + 5).Select(i => new HideRule { Query = $"@m = '{i}'" }).ToList();
        Assert.Equal(AppSettings.MaxListLength, new AppSettings { HideRules = many }.Normalised().HideRules.Count);
    }

    [Fact]
    public void Settings_with_equal_lists_are_equal()
    {
        var a = new AppSettings { HideRules = [new HideRule { Query = "@m = 'x'" }], SavedFilters = [new SavedFilter { Name = "n" }] };
        var b = new AppSettings { HideRules = [new HideRule { Query = "@m = 'x'" }], SavedFilters = [new SavedFilter { Name = "n" }] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { HideRules = [new HideRule { Query = "@m = 'x'", Enabled = false }] });
    }

    /// <summary>Every property takes part in equality: a property Equals forgets would make a change look like "nothing changed".</summary>
    [Fact]
    public void Changing_any_property_makes_the_settings_different()
    {
        var changed = new Dictionary<string, AppSettings>
        {
            [nameof(AppSettings.Version)] = new() { Version = 99 },
            [nameof(AppSettings.Theme)] = new() { Theme = ThemePreference.Dark },
            [nameof(AppSettings.FontFamily)] = new() { FontFamily = "Consolas" },
            [nameof(AppSettings.FontSize)] = new() { FontSize = 14 },
            [nameof(AppSettings.RowDensity)] = new() { RowDensity = RowDensity.Compact },
            [nameof(AppSettings.TimestampZone)] = new() { TimestampZone = TimestampZone.Utc },
            [nameof(AppSettings.TimestampFormat)] = new() { TimestampFormat = "HH:mm" },
            [nameof(AppSettings.RestoreSession)] = new() { RestoreSession = false },
            [nameof(AppSettings.TailByDefault)] = new() { TailByDefault = true },
            [nameof(AppSettings.NewestFirst)] = new() { NewestFirst = true },
            [nameof(AppSettings.WrapMessages)] = new() { WrapMessages = true },
            [nameof(AppSettings.ShowTimeline)] = new() { ShowTimeline = true },
            [nameof(AppSettings.Columns)] = new() { Columns = new ColumnLayout { ShowLevel = false } },
            [nameof(AppSettings.HintsSeen)] = new() { HintsSeen = true },
            [nameof(AppSettings.HideRules)] = new() { HideRules = [new HideRule { Query = "@m = 'x'" }] },
            [nameof(AppSettings.SavedFilters)] = new() { SavedFilters = [new SavedFilter { Name = "n" }] },
            [nameof(AppSettings.CustomTemplates)] = new() { CustomTemplates = [new CustomTemplate("n", "{Timestamp} {Level} {Message}")] },
        };

        var settable = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => p.Name);
        Assert.Equal(settable.Order(), changed.Keys.Order()); // a new property needs a line above (and in AppSettings.Equals)
        foreach (var (name, settings) in changed)
        {
            Assert.False(new AppSettings().Equals(settings), $"{name} is not compared");
        }
    }

    [Fact]
    public void Only_the_portable_part_travels_to_another_pc()
    {
        var here = new AppSettings { Theme = ThemePreference.Dark, HintsSeen = true, Columns = new ColumnLayout { TimeWidth = 99 } };

        var portable = here.Portable();
        Assert.Equal(ThemePreference.Dark, portable.Theme);
        Assert.False(portable.HintsSeen);
        Assert.Equal(new ColumnLayout(), portable.Columns);

        var imported = new AppSettings { Theme = ThemePreference.Light }.WithMachineStateOf(here);
        Assert.Equal(ThemePreference.Light, imported.Theme);
        Assert.True(imported.HintsSeen);
        Assert.Equal(here.Columns, imported.Columns);
    }

    [Fact]
    public void The_time_display_comes_from_the_zone_and_format()
    {
        var display = new AppSettings { TimestampZone = TimestampZone.Utc, TimestampFormat = "HH:mm" }.ToTimeDisplay();
        Assert.Equal(new TimeDisplay(TimestampZone.Utc, "HH:mm"), display);
    }

    [Fact]
    public void The_settings_service_normalises_what_it_loads_and_saves()
    {
        var store = new InMemorySettingsStore();
        store.Save(new AppSettings { FontSize = 1000 });

        var service = new SettingsService(store);
        Assert.Equal(AppSettings.MaxFontSize, service.Current.FontSize);

        service.Update(s => s with { FontSize = -3 });
        Assert.Equal(AppSettings.MinFontSize, store.Load().FontSize);
    }

    [Fact]
    public void A_hide_rule_is_listed_by_name_or_by_query()
    {
        Assert.Equal("Noise", new HideRule { Name = "Noise", Query = "@l = 'Verbose'" }.Label);
        Assert.Equal("@l = 'Verbose'", new HideRule { Query = "@l = 'Verbose'" }.Label);
    }
}

/// <summary>Font size, row density and timestamp format in the grid metrics (FR-P2, FR-G5).</summary>
public class GridMetricsSettingsTests
{
    [Fact]
    public void The_defaults_are_unchanged_by_the_new_options()
    {
        var m = GridMetrics.For(null, false, false, AppSettings.DefaultFontSize, RowDensity.Comfortable, TimeDisplay.DefaultFormat);

        Assert.Equal(GridMetrics.Default, m);
        Assert.Equal(GridMetrics.BaseRowHeight, m.RowHeight);
        Assert.Equal(GridMetrics.DefaultTimeColumnWidth, m.TimeColumnWidth);
        Assert.Equal(GridMetrics.DefaultLevelColumnWidth, m.LevelColumnWidth);
        Assert.Equal(GridMetrics.LineHeight, m.TextLineHeight);
        Assert.Equal(12, m.FontSize);
    }

    [Fact]
    public void Compact_rows_are_lower_but_still_fit_the_text()
    {
        var compact = GridMetrics.For(null, false, false, 12, RowDensity.Compact);

        Assert.True(compact.RowHeight < GridMetrics.BaseRowHeight);
        Assert.True(compact.RowHeight >= compact.TextLineHeight);
    }

    [Fact]
    public void A_larger_font_makes_rows_and_columns_larger()
    {
        var large = GridMetrics.For(null, true, false, 18);

        Assert.Equal(18, large.FontSize);
        Assert.True(large.RowHeight > GridMetrics.For(null, true, false).RowHeight);
        Assert.True(large.TimeColumnWidth > GridMetrics.DefaultTimeColumnWidth);
        Assert.True(large.LevelColumnWidth > GridMetrics.DefaultLevelColumnWidth);
        Assert.True(large.RowHeight >= large.MessageLines * large.TextLineHeight, "the wrapped lines must fit in the row");
    }

    [Fact]
    public void The_font_size_is_clamped() => Assert.Equal(AppSettings.MaxFontSize, GridMetrics.For(null, false, false, 72).FontSize);

    [Fact]
    public void The_time_column_fits_the_format()
    {
        var timeOnly = GridMetrics.For(null, false, false, timeFormat: "HH:mm:ss");

        Assert.True(timeOnly.TimeColumnWidth < GridMetrics.DefaultTimeColumnWidth);
        Assert.Equal(GridMetrics.DefaultTimeColumnWidth, GridMetrics.For(null, false, false, timeFormat: "%").TimeColumnWidth);
        Assert.Equal(120, GridMetrics.For(new ColumnLayout { TimeWidth = 120 }, false, false, timeFormat: "HH:mm:ss").TimeColumnWidth); // dragged wins
    }
}
