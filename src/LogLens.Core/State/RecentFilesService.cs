namespace LogLens.Core.State;

/// <summary>The recent files list (FR-O1): newest first, one entry per path (ignoring case), capped, saved on every change.</summary>
public sealed class RecentFilesService
{
    public const int DefaultMax = 10;

    private readonly IStateStore _store;
    private readonly int _max;
    private readonly Action<Exception>? _onSaveError;
    private List<string> _files;

    public RecentFilesService(IStateStore store, int max = DefaultMax, Action<Exception>? onSaveError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _max = max > 0 ? max : DefaultMax;
        _onSaveError = onSaveError;
        _files = Distinct(store.Load().RecentFiles).Take(_max).ToList();
    }

    public IReadOnlyList<string> Files => _files;

    public event EventHandler? Changed;

    /// <summary>Puts <paramref name="path"/> at the top.</summary>
    public void Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_files.Count > 0 && string.Equals(_files[0], path, StringComparison.Ordinal))
        {
            return;
        }

        Commit(Distinct([path, .. _files]).Take(_max).ToList());
    }

    public void Remove(string path)
    {
        var next = _files.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (next.Count != _files.Count)
        {
            Commit(next);
        }
    }

    public void Clear()
    {
        if (_files.Count > 0)
        {
            Commit([]);
        }
    }

    private static IEnumerable<string> Distinct(IEnumerable<string?> paths)
        => paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase);

    private void Commit(List<string> next)
    {
        _files = next;
        try
        {
            _store.Save(_store.Load() with { RecentFiles = next });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onSaveError?.Invoke(ex); // the list still works for this run
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
