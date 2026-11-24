using System.Text.Json;
using LogLens.Core.Settings;

namespace LogLens.Infrastructure.State;

/// <summary>
/// Keeps <see cref="AppSettings"/> in <c>settings.json</c> (SPEC §6.5), and writes and reads settings files for another PC (FR-P4).
/// </summary>
public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    /// <summary>A settings file to import is refused above this size: real ones are a few KB.</summary>
    public const long MaxImportBytes = 1024 * 1024;

    private const string NotASettingsFile = "This file is not a LogLens settings file.";

    private readonly JsonFileStore<AppSettings> _file = new(paths.SettingsFile, () => new AppSettings());

    /// <summary>Loads the settings with every unusable value put right (a hand-edited file may hold nulls, unknown names or odd sizes).</summary>
    public AppSettings Load() => _file.Load().Normalised();

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _file.Save(settings with { Version = AppSettings.CurrentVersion });
    }

    // ------------------------------------------------------------------ export / import (FR-P4)

    /// <summary>Writes <paramref name="settings"/> to <paramref name="path"/> for another PC, without this PC's column layout and hints flag.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Export(AppSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings.Normalised().Portable(), JsonFileStore<AppSettings>.Options));
    }

    /// <summary>
    /// Reads a settings file written by <see cref="Export"/> (or a copy of <c>settings.json</c>) and returns it on top of
    /// <paramref name="current"/>: unusable values are put right like a normal load, and this PC's column layout and hints flag are kept.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a settings file, or is too large to be one.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static AppSettings Import(string path, AppSettings current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(current);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxImportBytes)
        {
            throw new InvalidDataException("This file is too large to be a LogLens settings file.");
        }

        AppSettings? imported;
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("version", out _))
            {
                throw new InvalidDataException(NotASettingsFile);
            }

            imported = document.RootElement.Deserialize<AppSettings>(JsonFileStore<AppSettings>.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This file is not a valid settings file: " + ex.Message, ex);
        }

        if (imported is null)
        {
            throw new InvalidDataException(NotASettingsFile);
        }

        return (imported.Normalised() with { Version = AppSettings.CurrentVersion }).WithMachineStateOf(current);
    }
}
