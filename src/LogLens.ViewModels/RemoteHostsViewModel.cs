using LogLens.Core.Remote;

namespace LogLens.ViewModels;

/// <summary>A host in the list: one the user saved in LogLens, or one read from <c>~/.ssh/config</c>.</summary>
public sealed record RemoteHostItem(RemoteHost Host, bool FromConfig)
{
    /// <summary>Where the host comes from, shown at the end of its row.</summary>
    public string SourceLabel => FromConfig ? "~/.ssh/config" : Host.Kind == RemoteHostKind.Ssh ? "SSH" : "Windows share";

    /// <summary>What a screen reader says for the row.</summary>
    public string AccessibleName => $"{Host.DisplayName}, {Host.Summary}";
}

/// <summary>What the host form starts with: the fields to show, the saved host that saving replaces (null adds one), and the form's title.</summary>
public sealed record RemoteHostEdit(RemoteHost? Host, RemoteHost? Replaces, string Title);

/// <summary>
/// The host list of <em>File ▸ Open from remote host</em> (FR-O7): the saved hosts plus those in <c>~/.ssh/config</c> that no saved host
/// already covers, filtered by the text typed above the list, with the selection, the add / edit / duplicate / remove commands and the rules
/// for saving a host. Connecting and browsing are left to the view and <see cref="RemoteBrowserViewModel"/>.
/// </summary>
public sealed class RemoteHostsViewModel
{
    private readonly List<RemoteHost> _saved;
    private readonly IReadOnlyList<RemoteHost> _configHosts;
    private readonly Action<IReadOnlyList<RemoteHost>> _save;

    /// <param name="savedHosts">The hosts saved in LogLens.</param>
    /// <param name="configHosts">The hosts in <c>~/.ssh/config</c>.</param>
    /// <param name="save">Stores the saved hosts after one was added, changed or removed.</param>
    public RemoteHostsViewModel(IReadOnlyList<RemoteHost> savedHosts, IReadOnlyList<RemoteHost> configHosts, Action<IReadOnlyList<RemoteHost>> save)
    {
        ArgumentNullException.ThrowIfNull(savedHosts);
        ArgumentNullException.ThrowIfNull(configHosts);
        _saved = [.. savedHosts];
        _configHosts = configHosts;
        _save = save ?? throw new ArgumentNullException(nameof(save));
        Rebuild(select: null);
    }

    /// <summary>The hosts saved in LogLens, in order.</summary>
    public IReadOnlyList<RemoteHost> SavedHosts => _saved;

    /// <summary>The text typed above the list: only hosts whose name, address, user or port contain every word are listed.</summary>
    public string Filter { get; private set; } = string.Empty;

    /// <summary>The hosts listed: saved ones first, then those from <c>~/.ssh/config</c>, each kept only when it matches <see cref="Filter"/>.</summary>
    public IReadOnlyList<RemoteHostItem> Items { get; private set; } = [];

    /// <summary>How many hosts there are before filtering.</summary>
    public int Total { get; private set; }

    public RemoteHostItem? Selected { get; private set; }

    /// <summary>What the empty list says: there are no hosts yet, or none matches the filter.</summary>
    public string EmptyText => Total == 0
        ? "No hosts yet. Add an SSH host (for example an Ubuntu server) or a Windows share."
        : "No hosts match the filter. Change or clear it to see more hosts.";

    public bool CanConnect => Selected is not null;

    public bool CanEdit => Selected is not null;

    public bool CanDuplicate => Selected is not null;

    /// <summary>Only a host saved in LogLens can be removed; <c>~/.ssh/config</c> is never written.</summary>
    public bool CanRemove => Selected is { FromConfig: false };

    /// <summary>Lists the hosts matching <paramref name="filter"/>, keeping the selected host when it still matches.</summary>
    public void SetFilter(string? filter)
    {
        Filter = filter ?? string.Empty;
        Rebuild(select: Selected?.Host);
    }

    /// <summary>The user picked <paramref name="item"/> in the list (null: nothing).</summary>
    public void Select(RemoteHostItem? item) => Selected = item is not null && Items.Contains(item) ? item : null;

    /// <summary>The form for a new host.</summary>
    public static RemoteHostEdit AddHost() => new(null, null, "Add host");

    /// <summary>The form for the selected host; one from <c>~/.ssh/config</c> is saved as a new LogLens host. Null when nothing is selected.</summary>
    public RemoteHostEdit? EditSelected() => Selected switch
    {
        null => null,
        { FromConfig: true } item => new RemoteHostEdit(item.Host, null, "Save as a LogLens host"),
        var item => new RemoteHostEdit(item.Host, item.Host, "Edit host"),
    };

    /// <summary>The form filled in with the selected host's details under a name no host has yet, saved as a new host. Null when nothing is selected.</summary>
    public RemoteHostEdit? DuplicateSelected()
        => Selected is { } item
            ? new RemoteHostEdit(item.Host.Duplicate(_saved.Concat(_configHosts).Select(h => h.DisplayName)), null, "Duplicate host")
            : null;

    /// <summary>
    /// Saves <paramref name="host"/> in place of <paramref name="replaces"/>, or as a new host when that is null, and selects it (clearing the
    /// filter if it would hide it). Returns why it can't be saved instead, and changes nothing then.
    /// </summary>
    public string? Save(RemoteHost host, RemoteHost? replaces)
    {
        ArgumentNullException.ThrowIfNull(host);
        var error = host.Validate();
        if (error is null && _saved.Any(s => !ReferenceEquals(s, replaces) && string.Equals(s.DisplayName, host.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"There is already a host called {host.DisplayName}.";
        }

        if (error is not null)
        {
            return error;
        }

        var index = replaces is null ? -1 : _saved.IndexOf(replaces);
        if (index >= 0)
        {
            _saved[index] = host;
        }
        else
        {
            _saved.Add(host);
        }

        _save([.. _saved]);
        if (!host.Matches(Filter))
        {
            Filter = string.Empty; // a host just saved is shown even when the filter would hide it
        }

        Rebuild(select: host);
        return null;
    }

    /// <summary>Removes the selected host when it is one saved in LogLens.</summary>
    public void RemoveSelected()
    {
        if (Selected is { FromConfig: false } item && _saved.Remove(item.Host))
        {
            _save([.. _saved]);
            Rebuild(select: null);
        }
    }

    private void Rebuild(RemoteHost? select)
    {
        var items = _saved.Select(h => new RemoteHostItem(h, false)).ToList();

        // Hosts from ~/.ssh/config, unless a saved host already has that name or points at the same login.
        foreach (var config in _configHosts)
        {
            if (!_saved.Any(s => string.Equals(s.DisplayName, config.DisplayName, StringComparison.OrdinalIgnoreCase)
                                 || (s.Kind == RemoteHostKind.Ssh && string.Equals(s.Address, config.Address, StringComparison.OrdinalIgnoreCase)
                                     && s.UserName == config.UserName && s.Port == config.Port)))
            {
                items.Add(new RemoteHostItem(config, true));
            }
        }

        Total = items.Count;
        items.RemoveAll(item => !item.Host.Matches(Filter));
        Items = items;
        Selected = (select is null ? null : items.FirstOrDefault(i => i.Host.Equals(select))) ?? items.FirstOrDefault();
    }
}
