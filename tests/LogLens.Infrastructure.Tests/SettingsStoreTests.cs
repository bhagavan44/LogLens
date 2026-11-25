using LogLens.Core.Formats;
using LogLens.Core.Settings;
using LogLens.Infrastructure.State;

namespace LogLens.Infrastructure.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loglens-tests", Guid.NewGuid().ToString("N"));

    private AppPaths Paths => new(_dir, _dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        var settings = new JsonSettingsStore(Paths).Load();

        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public void The_file_lives_next_to_the_state_file()
    {
        Assert.Equal(Path.Combine(_dir, "LogLens", "settings.json"), Paths.SettingsFile);
        Assert.Equal(Path.GetDirectoryName(Paths.StateFile), Path.GetDirectoryName(Paths.SettingsFile));
    }

    [Fact]
    public void Settings_round_trip_through_the_file()
    {
        var store = new JsonSettingsStore(Paths);

        store.Save(new AppSettings
        {
            NewestFirst = true,
            WrapMessages = true,
            ShowTimeline = true,
            Columns = new ColumnLayout { ShowLevel = false, TimeWidth = 120, SourceWidth = 220, ShowContext = false, ContextWidth = 250 },
            CustomTemplates =
            [
                new CustomTemplate("Shop", "{Timestamp:HH:mm:ss} {Level:u3} {SourceContext} {Message}"),
                new CustomTemplate("Legacy", "%date %-5level %message%newline", TemplateKind.Log4Net),
            ],
        });

        var loaded = new JsonSettingsStore(Paths).Load();
        Assert.True(loaded.NewestFirst);
        Assert.True(loaded.WrapMessages);
        Assert.True(loaded.ShowTimeline);
        Assert.False(loaded.Columns.ShowLevel);
        Assert.Equal(120, loaded.Columns.TimeWidth);
        Assert.Equal(220, loaded.Columns.SourceWidth);
        Assert.False(loaded.Columns.ShowContext);
        Assert.Equal(250, loaded.Columns.ContextWidth);
        Assert.Equal(
            [
                new CustomTemplate("Shop", "{Timestamp:HH:mm:ss} {Level:u3} {SourceContext} {Message}"),
                new CustomTemplate("Legacy", "%date %-5level %message%newline", TemplateKind.Log4Net),
            ],
            loaded.CustomTemplates);
        Assert.False(File.Exists(Paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void A_hand_edited_file_with_a_null_layout_and_wild_widths_is_repaired()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
        File.WriteAllText(Paths.SettingsFile, """{ "newestFirst": true, "columns": { "timeWidth": 99999 } }""");
        var wild = new JsonSettingsStore(Paths).Load();
        Assert.True(wild.NewestFirst);
        Assert.Equal(ColumnLayout.MaxWidth, wild.Columns.TimeWidth);

        File.WriteAllText(Paths.SettingsFile, """{ "columns": null }""");
        Assert.Equal(new ColumnLayout(), new JsonSettingsStore(Paths).Load().Columns);
    }

    [Fact]
    public void Custom_templates_saved_before_kinds_existed_load_as_serilog_templates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
        File.WriteAllText(Paths.SettingsFile, """{ "customTemplates": [ { "name": "Shop", "template": "{Timestamp} {Level} {Message}" } ] }""");

        var template = Assert.Single(new JsonSettingsStore(Paths).Load().CustomTemplates);

        Assert.Equal(new CustomTemplate("Shop", "{Timestamp} {Level} {Message}", TemplateKind.Serilog), template);
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_the_defaults_are_used()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
        File.WriteAllText(Paths.SettingsFile, "{{{ nope");

        var settings = new JsonSettingsStore(Paths).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(File.Exists(Paths.SettingsFile + ".corrupt"));
    }

    [Fact]
    public void The_service_keeps_a_change_across_restarts()
    {
        new SettingsService(new JsonSettingsStore(Paths)).Update(s => s with { NewestFirst = true, Columns = s.Columns with { TimeWidth = 80 } });

        var again = new SettingsService(new JsonSettingsStore(Paths)).Current;

        Assert.True(again.NewestFirst);
        Assert.Equal(80, again.Columns.TimeWidth);
    }
}
