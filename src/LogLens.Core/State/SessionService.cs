namespace LogLens.Core.State;

/// <summary>
/// Remembers the open logs so the next start reopens them (FR-P3). Saved whenever they change rather than only on exit, so a crash or a
/// killed process still reopens what was being looked at. Saving never throws into the UI.
/// </summary>
public sealed class SessionService
{
    /// <summary>More logs than this are not remembered: a session is a working set, not an archive.</summary>
    public const int MaxTabs = 20;

    private readonly IStateStore _store;
    private readonly Action<Exception>? _onSaveError;
    private SessionState _last;

    public SessionService(IStateStore store, Action<Exception>? onSaveError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onSaveError = onSaveError;
        _last = Sanitise(store.Load().Session);
    }

    /// <summary>The session as last saved (or loaded), without invalid entries.</summary>
    public SessionState Current => _last;

    public void Save(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var next = Sanitise(session);
        if (next.Selected == _last.Selected && next.Tabs.SequenceEqual(_last.Tabs, TabComparer.Instance))
        {
            return; // nothing changed: leave the disk alone
        }

        _last = next;
        try
        {
            _store.Save(_store.Load() with { Session = next });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onSaveError?.Invoke(ex);
        }
    }

    private static SessionState Sanitise(SessionState? session)
    {
        var tabs = (session?.Tabs ?? [])
            .Where(t => t is not null && t.IsValid())
            .Select(t => t.MergedFiles is null ? t : t with { Path = null, MergedFiles = [.. t.MergedFiles.Where(f => !string.IsNullOrWhiteSpace(f))] })
            .Take(MaxTabs)
            .ToList();
        var selected = tabs.Count == 0 ? 0 : Math.Clamp(session?.Selected ?? 0, 0, tabs.Count - 1);
        return new SessionState { Tabs = tabs, Selected = selected };
    }

    private sealed class TabComparer : IEqualityComparer<SessionTab>
    {
        public static readonly TabComparer Instance = new();

        public bool Equals(SessionTab? x, SessionTab? y)
            => x is not null
               && y is not null
               && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase)
               && x.Follow == y.Follow
               && (x.MergedFiles ?? []).SequenceEqual(y.MergedFiles ?? [], StringComparer.OrdinalIgnoreCase);

        public int GetHashCode(SessionTab obj) => 0;
    }
}
