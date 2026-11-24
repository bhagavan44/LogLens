using LogLens.Core.Models;
using LogLens.Core.Settings;
using LogLens.Infrastructure.State;

namespace LogLens.Infrastructure.Tests;

/// <summary>The v1.0 settings in <c>settings.json</c>, and settings files for another PC (FR-P4).</summary>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loglens-tests", Guid.NewGuid().ToString("N"));

    public SettingsTransferTests() => Directory.CreateDirectory(_dir);

    private AppPaths Paths => new(_dir, _dir);

    private string File(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AppSettings Everything() => new()
    {
        Theme = ThemePreference.Dark,
        FontFamily = "Consolas",
        FontSize = 14,
        RowDensity = RowDensity.Compact,
        TimestampZone = TimestampZone.Utc,
        TimestampFormat = "HH:mm:ss.fff",
        RestoreSession = false,
        TailByDefault = true,
        HideRules = [new HideRule { Name = "Health", Query = "@m startswith 'Health'", Enabled = false }],
        SavedFilters = [new SavedFilter { Name = "Errors", Query = "@x contains 'Timeout'", Levels = 0b110000 }],
        Columns = new ColumnLayout { TimeWidth = 99 },
        HintsSeen = true,
    };

    [Fact]
    public void Every_setting_round_trips_through_the_file()
    {
        var store = new JsonSettingsStore(Paths);

        store.Save(Everything());

        Assert.Equal(Everything(), new JsonSettingsStore(Paths).Load());
    }

    [Fact]
    public void Enums_are_written_by_name_and_computed_values_are_not_written()
    {
        new JsonSettingsStore(Paths).Save(Everything());
        var json = System.IO.File.ReadAllText(Paths.SettingsFile);

        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"timestampZone\": \"utc\"", json);
        Assert.DoesNotContain("\"label\"", json);
    }

    [Fact]
    public void A_hand_edited_file_with_bad_values_is_put_right()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
        System.IO.File.WriteAllText(Paths.SettingsFile, """
            {
              "version": 1,
              "fontSize": 300,
              "fontFamily": "",
              "timestampFormat": "%",
              "hideRules": [ null, { "query": "  " }, { "query": "@l = 'Debug'" } ],
              "savedFilters": null
            }
            """);

        var settings = new JsonSettingsStore(Paths).Load();

        Assert.Equal(AppSettings.MaxFontSize, settings.FontSize);
        Assert.Equal(AppSettings.DefaultFontFamily, settings.FontFamily);
        Assert.Equal(TimeDisplay.DefaultFormat, settings.TimestampFormat);
        Assert.Equal("@l = 'Debug'", Assert.Single(settings.HideRules).Query);
        Assert.Empty(settings.SavedFilters);
    }

    [Fact]
    public void An_export_leaves_out_this_pcs_columns_and_hints()
    {
        var path = File("export.json");

        JsonSettingsStore.Export(Everything(), path);

        var json = System.IO.File.ReadAllText(path);
        Assert.Contains("\"hintsSeen\": false", json);
        Assert.DoesNotContain("99", json);
    }

    [Fact]
    public void An_import_brings_the_portable_settings_and_keeps_this_pcs_own()
    {
        var path = File("export.json");
        JsonSettingsStore.Export(Everything(), path);
        var here = new AppSettings { Columns = new ColumnLayout { ShowLevel = false }, HintsSeen = false };

        var imported = JsonSettingsStore.Import(path, here);

        Assert.Equal(Everything().WithMachineStateOf(here), imported);
        Assert.Equal("Errors", Assert.Single(imported.SavedFilters).Name); // saved filters travel too
    }

    [Fact]
    public void A_copy_of_settings_json_can_be_imported()
    {
        new JsonSettingsStore(Paths).Save(Everything());

        var imported = JsonSettingsStore.Import(Paths.SettingsFile, new AppSettings());

        Assert.Equal(ThemePreference.Dark, imported.Theme);
        Assert.False(imported.HintsSeen);
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"theme\": \"dark\" }")] // no version: some other JSON file
    [InlineData("{ \"version\": 1, ")]
    [InlineData("not json at all")]
    public void A_file_that_is_not_a_settings_file_is_refused(string text)
    {
        var path = File("other.json");
        System.IO.File.WriteAllText(path, text);

        Assert.Throws<InvalidDataException>(() => JsonSettingsStore.Import(path, new AppSettings()));
    }

    [Fact]
    public void A_huge_file_is_refused_without_reading_it()
    {
        var path = File("huge.json");
        using (var stream = System.IO.File.Create(path))
        {
            stream.SetLength(JsonSettingsStore.MaxImportBytes + 1);
        }

        Assert.Throws<InvalidDataException>(() => JsonSettingsStore.Import(path, new AppSettings()));
    }

    [Fact]
    public void Imported_values_are_put_right_like_a_load()
    {
        var path = File("odd.json");
        System.IO.File.WriteAllText(path, """{ "version": 1, "fontSize": 2, "theme": "light" }""");

        var imported = JsonSettingsStore.Import(path, new AppSettings());

        Assert.Equal(AppSettings.MinFontSize, imported.FontSize);
        Assert.Equal(ThemePreference.Light, imported.Theme);
    }
}
