using LogLens.Core.Remote;

namespace LogLens.ViewModels.Tests;

/// <summary>The host list of <em>Open from remote host</em> (FR-O7): what is listed, filtering, selection and saving.</summary>
public class RemoteHostsViewModelTests
{
    private static readonly RemoteHost Web = new() { Name = "web01", Address = "web01.example.com", UserName = "deploy" };
    private static readonly RemoteHost Db = new() { Name = "db01", Address = "db01.example.com", UserName = "admin" };
    private static readonly RemoteHost Share = new() { Kind = RemoteHostKind.WindowsShare, Name = "File server", Address = @"\\srv\logs" };

    private sealed class Store
    {
        public List<IReadOnlyList<RemoteHost>> Saves { get; } = [];

        public void Save(IReadOnlyList<RemoteHost> hosts) => Saves.Add(hosts);
    }

    private static (RemoteHostsViewModel Vm, Store Store) Make(IReadOnlyList<RemoteHost> saved, params RemoteHost[] config)
    {
        var store = new Store();
        return (new RemoteHostsViewModel(saved, config, store.Save), store);
    }

    private static string[] Names(RemoteHostsViewModel vm) => [.. vm.Items.Select(i => i.Host.DisplayName)];

    [Fact]
    public void Lists_saved_hosts_then_config_hosts_that_no_saved_host_covers()
    {
        var sameLogin = new RemoteHost { Name = "web", Address = "WEB01.example.com", UserName = "deploy" }; // web01 under another name
        var sameName = new RemoteHost { Name = "DB01", Address = "10.0.0.5", UserName = "root" };
        var other = new RemoteHost { Name = "build", Address = "build.example.com", UserName = "ci" };

        var (vm, _) = Make([Web, Db, Share], sameLogin, sameName, other);

        Assert.Equal(["web01", "db01", "File server", "build"], Names(vm));
        Assert.Equal(4, vm.Total);
        Assert.Equal(["SSH", "SSH", "Windows share", "~/.ssh/config"], vm.Items.Select(i => i.SourceLabel));
        Assert.Equal("web01, deploy@web01.example.com", vm.Items[0].AccessibleName);
    }

    [Fact]
    public void The_first_host_is_selected_and_the_buttons_follow_the_selection()
    {
        var config = new RemoteHost { Name = "build", Address = "build.example.com" };
        var (vm, _) = Make([Web], config);

        Assert.Same(vm.Items[0], vm.Selected);
        Assert.True(vm.CanConnect && vm.CanEdit && vm.CanDuplicate && vm.CanRemove);

        vm.Select(vm.Items[1]);
        Assert.True(vm.CanConnect);
        Assert.False(vm.CanRemove); // ~/.ssh/config is never written

        vm.Select(null);
        Assert.False(vm.CanConnect || vm.CanEdit || vm.CanDuplicate || vm.CanRemove);
    }

    [Fact]
    public void Filtering_keeps_the_selected_host_when_it_still_matches()
    {
        var (vm, _) = Make([Web, Db, Share]);
        vm.Select(vm.Items[1]); // db01

        vm.SetFilter("example");

        Assert.Equal(["web01", "db01"], Names(vm));
        Assert.Equal("db01", vm.Selected?.Host.DisplayName);
    }

    [Fact]
    public void Filtering_out_the_selected_host_selects_the_first_match_and_keeps_the_filter()
    {
        var (vm, _) = Make([Web, Db, Share]);
        Assert.Equal("web01", vm.Selected?.Host.DisplayName);

        vm.SetFilter("db");

        Assert.Equal("db", vm.Filter); // typing another host's name must not wipe the filter
        Assert.Equal(["db01"], Names(vm));
        Assert.Equal("db01", vm.Selected?.Host.DisplayName);
    }

    [Fact]
    public void The_empty_list_says_whether_there_are_no_hosts_or_none_matches()
    {
        var (none, _) = Make([]);
        Assert.Empty(none.Items);
        Assert.Null(none.Selected);
        Assert.StartsWith("No hosts yet", none.EmptyText);

        var (vm, _) = Make([Web]);
        vm.SetFilter("nothing like it");
        Assert.Empty(vm.Items);
        Assert.Null(vm.Selected);
        Assert.StartsWith("No hosts match the filter", vm.EmptyText);
    }

    [Fact]
    public void The_host_form_is_set_up_for_adding_editing_and_duplicating()
    {
        var config = new RemoteHost { Name = "build", Address = "build.example.com" };
        var (vm, _) = Make([Web], config);

        Assert.Equal(new RemoteHostEdit(null, null, "Add host"), RemoteHostsViewModel.AddHost());
        Assert.Equal(new RemoteHostEdit(Web, Web, "Edit host"), vm.EditSelected());

        var copy = vm.DuplicateSelected()!;
        Assert.Equal("web01 (copy)", copy.Host!.DisplayName);
        Assert.Equal(Web.Address, copy.Host.Address);
        Assert.Null(copy.Replaces);
        Assert.Equal("Duplicate host", copy.Title);

        vm.Select(vm.Items[1]);
        Assert.Equal(new RemoteHostEdit(config, null, "Save as a LogLens host"), vm.EditSelected());

        vm.Select(null);
        Assert.Null(vm.EditSelected());
        Assert.Null(vm.DuplicateSelected());
    }

    [Fact]
    public void Saving_a_new_host_stores_it_and_selects_it()
    {
        var (vm, store) = Make([Web]);

        Assert.Null(vm.Save(Db, replaces: null));

        Assert.Equal([Web, Db], vm.SavedHosts);
        Assert.Equal([Web, Db], Assert.Single(store.Saves));
        Assert.Equal("db01", vm.Selected?.Host.DisplayName);
    }

    [Fact]
    public void Saving_an_edited_host_replaces_it_in_place()
    {
        var (vm, store) = Make([Web, Db]);
        var renamed = Web with { Name = "web-prod" };

        Assert.Null(vm.Save(renamed, replaces: Web));

        Assert.Equal([renamed, Db], vm.SavedHosts);
        Assert.Single(store.Saves);
        Assert.Same(renamed, vm.Selected?.Host);
    }

    [Fact]
    public void A_host_saved_while_the_filter_would_hide_it_clears_the_filter()
    {
        var (vm, _) = Make([Web, Db]);
        vm.SetFilter("web");

        Assert.Null(vm.Save(Share, replaces: null));

        Assert.Equal(string.Empty, vm.Filter);
        Assert.Equal(["web01", "db01", "File server"], Names(vm));
        Assert.Equal("File server", vm.Selected?.Host.DisplayName);
    }

    [Fact]
    public void A_host_that_cant_be_saved_changes_nothing()
    {
        var (vm, store) = Make([Web, Db]);

        Assert.NotNull(vm.Save(new RemoteHost { Name = "empty" }, replaces: null)); // no address
        Assert.Equal("There is already a host called DB01.", vm.Save(Web with { Name = "DB01" }, replaces: Web));

        Assert.Equal([Web, Db], vm.SavedHosts);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public void A_host_keeps_its_own_name_when_edited()
    {
        var (vm, _) = Make([Web]);

        Assert.Null(vm.Save(Web with { UserName = "root" }, replaces: Web));
    }

    [Fact]
    public void Removing_deletes_only_a_saved_host()
    {
        var config = new RemoteHost { Name = "build", Address = "build.example.com" };
        var (vm, store) = Make([Web, Db], config);
        vm.Select(vm.Items[2]);

        vm.RemoveSelected(); // from ~/.ssh/config: nothing happens
        Assert.Empty(store.Saves);
        Assert.Equal(3, vm.Items.Count);

        vm.Select(vm.Items[0]);
        vm.RemoveSelected();

        Assert.Equal([Db], vm.SavedHosts);
        Assert.Equal([Db], Assert.Single(store.Saves));
        Assert.Equal(["db01", "build"], Names(vm));
        Assert.Equal("db01", vm.Selected?.Host.DisplayName);
    }
}
