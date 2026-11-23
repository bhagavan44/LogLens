using System.Text.Json;
using System.Text.Json.Serialization;
using LogLens.Core.State;

namespace LogLens.Infrastructure.State;

/// <summary>Keeps <see cref="AppState"/> in <c>state.json</c> (SPEC §6.5).</summary>
public sealed class JsonStateStore(AppPaths paths) : IStateStore
{
    private readonly JsonFileStore<AppState> _file = new(paths.StateFile, () => new AppState());

    public AppState Load()
    {
        // A hand-edited file may hold nulls where lists belong.
        var state = _file.Load();
        return state with
        {
            RecentFiles = state.RecentFiles ?? [],
            Session = state.Session is { } session ? session with { Tabs = session.Tabs ?? [] } : new SessionState(),
        };
    }

    public void Save(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _file.Save(state with { Version = AppState.CurrentVersion });
    }
}

/// <summary>
/// Reads and writes one JSON document. A write goes to a temporary file that is then swapped in, so a crash mid-write can't leave a truncated
/// file; an unreadable file is moved aside (<c>*.corrupt</c>) and the default value is used.
/// </summary>
internal sealed class JsonFileStore<T>(string path, Func<T> createDefault)
    where T : class
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();

    public T Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return createDefault();
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream, Options) ?? createDefault();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                MoveAside();
                return createDefault();
            }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }

    private void MoveAside()
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left in place; it is read again (and fails again) next time
        }
    }
}
