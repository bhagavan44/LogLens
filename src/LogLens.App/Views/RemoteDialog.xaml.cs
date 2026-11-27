using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using LogLens.App.Services;
using LogLens.Core.Remote;
using LogLens.Infrastructure.Remote;
using LogLens.ViewModels;

namespace LogLens.App.Views;

/// <summary>
/// File ▸ Open from remote host (FR-O7): the saved hosts (and those in <c>~/.ssh/config</c>), adding and editing them, connecting —
/// including the password, passphrase and host-key questions, asked right here because only one dialog can be open at a time — and
/// browsing the host's folders to pick files. The host list lives in <see cref="RemoteHostsViewModel"/> and browsing state in
/// <see cref="RemoteBrowserViewModel"/>; this class only shows them.
/// </summary>
public sealed partial class RemoteDialog : ContentDialog, ISshPrompt
{
    public enum Result
    {
        None,
        Tabs,
        Merge,
    }

    private enum Stage
    {
        Hosts,
        Edit,
        Connecting,
        Prompt,
        Browse,
    }

    private readonly SftpConnections _connections;
    private readonly IHostKeyTrust _trust;
    private readonly RemoteHostsViewModel _hosts;
    private readonly Func<Task<string?>> _pickKeyFile;
    private readonly string _initialPatterns;

    private Stage _stage;
    private RemoteHost? _editing;
    private CancellationTokenSource? _connectCts;
    private TaskCompletionSource<string?>? _prompt;
    private RemoteBrowserViewModel? _browser;
    private readonly List<(Button Crumb, Button Arrow)> _crumbs = [];
    private Button? _crumbOverflow;
    private int _hiddenCrumbs;
    private CancellationTokenSource? _subFolders;
    private bool _closed;
    private bool _clearingFilter;
    private bool _showingHosts;

    public RemoteDialog(
        IReadOnlyList<RemoteHost> savedHosts,
        IReadOnlyList<RemoteHost> configHosts,
        SftpConnections connections,
        IHostKeyTrust trust,
        Action<IReadOnlyList<RemoteHost>> saveHosts,
        string patterns,
        Func<Task<string?>> pickKeyFile)
    {
        _hosts = new RemoteHostsViewModel(savedHosts, configHosts, saveHosts);
        _connections = connections;
        _trust = trust;
        _initialPatterns = patterns;
        _pickKeyFile = pickKeyFile;
        InitializeComponent();
        ShowHosts();
        ShowStage(Stage.Hosts);
    }

    /// <summary>What the user chose to do with <see cref="SelectedPaths"/>.</summary>
    public Result Choice { get; private set; }

    /// <summary>The ticked files (<c>sftp://</c> locations or share paths), by name — date order for rolling logs.</summary>
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    /// <summary>The file pattern the user ended up with, so it can be remembered.</summary>
    public string Patterns => _browser?.Patterns ?? _initialPatterns;

    // ---------------------------------------------------------------- stages and dialog buttons

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        HostsPanel.Visibility = Visible(stage == Stage.Hosts);
        EditPanel.Visibility = Visible(stage == Stage.Edit);
        ConnectingPanel.Visibility = Visible(stage == Stage.Connecting);
        PromptPanel.Visibility = Visible(stage == Stage.Prompt);
        BrowsePanel.Visibility = Visible(stage == Stage.Browse);

        SecondaryButtonText = string.Empty;
        switch (stage)
        {
            case Stage.Hosts:
                PrimaryButtonText = "Connect";
                UpdateHostButtons();
                break;
            case Stage.Edit:
                PrimaryButtonText = "Save";
                IsPrimaryButtonEnabled = true;
                break;
            case Stage.Connecting:
                PrimaryButtonText = string.Empty;
                break;
            case Stage.Prompt:
                IsPrimaryButtonEnabled = true; // the text is set by the question
                break;
            case Stage.Browse:
                PrimaryButtonText = "Open in tabs";
                SecondaryButtonText = "Merge into one view";
                UpdateBrowseButtons();
                GuardedSynchronizationContext.Enqueue(DispatcherQueue, LayoutCrumbs); // once the path bar has its width
                break;
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The dialog puts its content in a scroll viewer, so in a window too low for the content the buttons under the lists end up
    /// out of sight. Without vertical scrolling the content is measured against the height that is left and asks for at most
    /// that (the 430-high border only sets the size wanted); the lists' rows then shrink instead. The edit form scrolls by itself.
    /// </summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        for (var node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(Root); node is not null && !ReferenceEquals(node, this); node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller)
            {
                scroller.VerticalScrollMode = ScrollMode.Disabled;
                scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                return;
            }
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        switch (_stage)
        {
            case Stage.Browse when FileList.SelectedItems.Count > 0:
                Finish(Result.Tabs);
                return; // let the dialog close
            case Stage.Hosts:
                if (_hosts.Selected is { } item)
                {
                    _ = ConnectAsync(item.Host);
                }

                break;
            case Stage.Edit:
                SaveEdit();
                break;
            case Stage.Prompt:
                _prompt?.TrySetResult(PromptPassword.Visibility == Visibility.Visible ? PromptPassword.Password : "yes");
                break;
        }

        args.Cancel = true; // every other stage stays in the dialog
    }

    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_stage == Stage.Browse && FileList.SelectedItems.Count > 1)
        {
            Finish(Result.Merge);
            return;
        }

        args.Cancel = true;
    }

    private void Finish(Result choice)
    {
        Choice = choice;
        SelectedPaths = FileList.SelectedItems.OfType<ListViewItem>().Select(i => (string)i.Tag).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _closed = true;
        _connectCts?.Cancel();
        _prompt?.TrySetResult(null);
        if (_browser is not null)
        {
            _browser.PropertyChanged -= OnBrowserChanged;
        }
    }

    private void OnBackToHostsClick(object sender, RoutedEventArgs e)
    {
        StopBrowsing();
        HostsError.Visibility = Visibility.Collapsed;
        ShowStage(Stage.Hosts);
    }

    // ---------------------------------------------------------------- hosts

    /// <summary>Shows <see cref="RemoteHostsViewModel"/>'s list, filter text, selection and the buttons that depend on it.</summary>
    private void ShowHosts()
    {
        _showingHosts = true; // clearing and refilling the list raises selection changes that are not the user's
        try
        {
            if (HostFilterBox.Text != _hosts.Filter)
            {
                _clearingFilter = true;
                HostFilterBox.Text = _hosts.Filter;
                _clearingFilter = false;
            }

            var selected = _hosts.Selected;
            HostList.Items.Clear();
            ListViewItem? selectedRow = null;
            foreach (var item in _hosts.Items)
            {
                var row = HostRow(item);
                HostList.Items.Add(row);
                if (ReferenceEquals(item, selected))
                {
                    selectedRow = row;
                }
            }

            HostList.SelectedItem = selectedRow;
        }
        finally
        {
            _showingHosts = false;
        }

        NoHostsText.Text = _hosts.EmptyText;
        NoHostsText.Visibility = Visible(_hosts.Items.Count == 0);
        UpdateHostButtons();
    }

    private ListViewItem HostRow(RemoteHostItem item)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new FontIcon { Glyph = item.Host.Kind == RemoteHostKind.Ssh ? "\uE756" : "\uE8B7", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Host.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = item.Host.Summary, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["SecondaryCaption"] });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var source = new TextBlock { Text = item.SourceLabel, Style = (Style)Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(source, 2);
        row.Children.Add(source);

        var listItem = new ListViewItem { Content = row, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(listItem, item.AccessibleName);
        return listItem;
    }

    private void OnHostFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!_clearingFilter)
        {
            _hosts.SetFilter(HostFilterBox.Text); // keeps the selected host when it still matches
            ShowHosts();
        }
    }

    private void OnHostFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down when HostList.Items.Count > 0:
                e.Handled = true;
                (HostList.SelectedItem as ListViewItem ?? (ListViewItem)HostList.Items[0]).Focus(FocusState.Keyboard);
                break;
            case Windows.System.VirtualKey.Escape when HostFilterBox.Text.Length > 0:
                e.Handled = true; // clear the filter, not close the dialog
                HostFilterBox.Text = string.Empty;
                break;
        }
    }

    private void UpdateHostButtons()
    {
        EditHostButton.IsEnabled = _hosts.CanEdit;
        DuplicateHostButton.IsEnabled = _hosts.CanDuplicate;
        RemoveHostButton.IsEnabled = _hosts.CanRemove;
        if (_stage == Stage.Hosts)
        {
            IsPrimaryButtonEnabled = _hosts.CanConnect;
        }
    }

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_showingHosts)
        {
            _hosts.Select((HostList.SelectedItem as ListViewItem)?.Tag as RemoteHostItem);
            UpdateHostButtons();
        }
    }

    private void OnHostDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_hosts.Selected is { } item)
        {
            _ = ConnectAsync(item.Host);
        }
    }

    private void OnAddHostClick(object sender, RoutedEventArgs e) => BeginEdit(RemoteHostsViewModel.AddHost());

    private void OnEditHostClick(object sender, RoutedEventArgs e)
    {
        if (_hosts.EditSelected() is { } edit)
        {
            BeginEdit(edit);
        }
    }

    /// <summary>Opens the host form filled in with the selected host's details, saved as a new host.</summary>
    private void OnDuplicateHostClick(object sender, RoutedEventArgs e)
    {
        if (_hosts.DuplicateSelected() is { } edit)
        {
            BeginEdit(edit);
        }
    }

    private void OnRemoveHostClick(object sender, RoutedEventArgs e)
    {
        _hosts.RemoveSelected();
        ShowHosts();
    }

    private void ShowHostsError(string message)
    {
        HostsError.Text = message;
        HostsError.Visibility = Visibility.Visible;
        ShowStage(Stage.Hosts);
    }

    // ---------------------------------------------------------------- connecting

    private async Task ConnectAsync(RemoteHost host)
    {
        HostsError.Visibility = Visibility.Collapsed;
        if (host.Kind == RemoteHostKind.WindowsShare)
        {
            StartBrowsing(new ShareFolders(host));
            return;
        }

        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();
        ConnectingText.Text = $"Connecting to {host.Summary}…";
        ShowStage(Stage.Connecting);

        try
        {
            var connection = await _connections.ConnectAsync(SshTarget.From(host), this, cts.Token);
            if (!cts.IsCancellationRequested && !_closed)
            {
                StartBrowsing(new SftpFolders(connection, host));
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                ShowStage(Stage.Hosts);
            }
        }
        catch (IOException ex)
        {
            if (!_closed && !cts.IsCancellationRequested)
            {
                ShowHostsError(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            cts.Dispose();
        }
    }

    public Task<string?> AskPasswordAsync(SshTarget target, string? problem, CancellationToken cancellationToken)
        => AskAsync(
            "Password",
            $"Enter the password of {target.UserName} on {target.Host}. It is kept in memory only, so a dropped connection can be re-established until LogLens closes.",
            fingerprint: null,
            password: true,
            primary: "Connect",
            problem,
            cancellationToken);

    public Task<string?> AskPassphraseAsync(SshTarget target, string keyFile, string? problem, CancellationToken cancellationToken)
        => AskAsync(
            "Key passphrase",
            $"The key {keyFile} is protected by a passphrase. Enter it to sign in to {target.Host}, or go back to skip this key.",
            fingerprint: null,
            password: true,
            primary: "Unlock",
            problem,
            cancellationToken);

    public async Task<bool> ConfirmHostKeyAsync(SshTarget target, HostKeyInfo key, CancellationToken cancellationToken)
        => await AskAsync(
            "Unknown host",
            $"{target.Host} hasn't been used before: its host key isn't in your known_hosts file and wasn't accepted in LogLens. " +
            $"Check that the fingerprint below matches the server's (on the server: ssh-keygen -lf /etc/ssh/ssh_host_{KeyFileName(key.KeyType)}_key.pub) before trusting it.",
            fingerprint: $"{key.KeyType}\n{key.Fingerprint}",
            password: false,
            primary: "Trust and connect",
            problem: null,
            cancellationToken) is not null;

    private static string KeyFileName(string keyType) => keyType switch
    {
        "ssh-ed25519" => "ed25519",
        "ssh-rsa" => "rsa",
        var t when t.StartsWith("ecdsa", StringComparison.Ordinal) => "ecdsa",
        _ => "*",
    };

    /// <summary>Shows a question inside the dialog and waits for the answer: the text typed (or "yes"), or null for cancel.</summary>
    private async Task<string?> AskAsync(string title, string message, string? fingerprint, bool password, string primary, string? problem, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return null;
        }

        _prompt?.TrySetResult(null);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompt = answer;

        PromptTitle.Text = title;
        PromptMessage.Text = message;
        PromptFingerprint.Text = fingerprint ?? string.Empty;
        PromptFingerprint.Visibility = Visible(fingerprint is not null);
        PromptPassword.Password = string.Empty;
        PromptPassword.Visibility = Visible(password);
        AutomationProperties.SetName(PromptPassword, title);
        PromptProblem.Text = problem ?? string.Empty;
        PromptProblem.Visibility = Visible(problem is not null);
        PrimaryButtonText = primary;
        ShowStage(Stage.Prompt);
        if (password)
        {
            PromptPassword.Focus(FocusState.Programmatic);
        }

        using var registration = cancellationToken.Register(() => answer.TrySetResult(null));
        var result = await answer.Task;
        PromptPassword.Password = string.Empty; // don't leave it in the control
        if (ReferenceEquals(_prompt, answer))
        {
            _prompt = null;
        }

        if (_stage == Stage.Prompt && !_closed)
        {
            ShowStage(result is null ? Stage.Hosts : Stage.Connecting);
        }

        return result;
    }

    private void OnPromptBackClick(object sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel();
        _prompt?.TrySetResult(null);
        ShowStage(Stage.Hosts);
    }

    // ---------------------------------------------------------------- adding and editing a host

    /// <summary>Shows the host form with <paramref name="edit"/>'s fields; saving replaces its host, or adds one when it replaces none.</summary>
    private void BeginEdit(RemoteHostEdit edit)
    {
        var host = edit.Host;
        _editing = edit.Replaces;
        EditTitle.Text = edit.Title;
        var h = host ?? new RemoteHost();
        KindBox.SelectedIndex = (int)h.Kind;
        NameBox.Text = host is null ? string.Empty : h.Name;
        AddressBox.Text = h.Address;
        PortBox.Value = h.Port;
        UserBox.Text = h.UserName;
        KeyFileBox.Text = h.KeyFile;
        StartFolderBox.Text = h.StartFolder;
        ForgetKeyButton.Visibility = Visible(host is { Kind: RemoteHostKind.Ssh });
        ForgetKeyResult.Text = string.Empty;
        EditError.Visibility = Visibility.Collapsed;
        UpdateKindFields();
        ShowStage(Stage.Edit);
        NameBox.Focus(FocusState.Programmatic);
    }

    private RemoteHostKind EditKind => KindBox.SelectedIndex == 1 ? RemoteHostKind.WindowsShare : RemoteHostKind.Ssh;

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => UpdateKindFields();

    private void UpdateKindFields()
    {
        if (AddressBox is null)
        {
            return; // still loading the XAML
        }

        var ssh = EditKind == RemoteHostKind.Ssh;
        AddressBox.Header = ssh ? "Host name or IP address" : "Share folder";
        AddressBox.PlaceholderText = ssh ? "web01.example.com" : @"\\server\share\logs";
        PortBox.Visibility = Visible(ssh);
        SshFields.Visibility = Visible(ssh);
        ShareHint.Visibility = Visible(!ssh);
    }

    private RemoteHost ReadFields() => new RemoteHost
    {
        Kind = EditKind,
        Name = NameBox.Text,
        Address = AddressBox.Text,
        Port = double.IsNaN(PortBox.Value) ? RemoteHost.DefaultSshPort : (int)PortBox.Value,
        UserName = EditKind == RemoteHostKind.Ssh ? UserBox.Text : string.Empty,
        KeyFile = EditKind == RemoteHostKind.Ssh ? KeyFileBox.Text : string.Empty,
        StartFolder = EditKind == RemoteHostKind.Ssh ? StartFolderBox.Text : string.Empty,
    }.Normalised();

    private void SaveEdit()
    {
        if (_hosts.Save(ReadFields(), _editing) is { } error)
        {
            EditError.Text = error;
            EditError.Visibility = Visibility.Visible;
            return;
        }

        ShowHosts();
        ShowStage(Stage.Hosts);
    }

    private async void OnBrowseKeyClick(object sender, RoutedEventArgs e)
    {
        if (await _pickKeyFile() is { } path)
        {
            KeyFileBox.Text = path;
        }
    }

    private void OnForgetKeyClick(object sender, RoutedEventArgs e)
    {
        var host = ReadFields();
        _trust.Forget(host.Address, host.Port);
        ForgetKeyResult.Text = "Forgotten: the key will be checked again on the next connect.";
    }

    // ---------------------------------------------------------------- browsing

    private void StartBrowsing(IRemoteFolders folders)
    {
        StopBrowsing();
        var browser = _browser = new RemoteBrowserViewModel(folders, _initialPatterns);
        browser.PropertyChanged += OnBrowserChanged;
        BrowseHost.Text = folders.DisplayName;
        PatternBox.Text = browser.Patterns;
        FilterBox.Text = string.Empty;
        SortBox.SelectedIndex = 2; // Modified (newest first), the view-model's default
        EndTypingPath();
        RebuildCrumbs();
        RebuildBrowseLists();
        ShowStage(Stage.Browse);
        _ = browser.LoadAsync();
    }

    private void StopBrowsing()
    {
        if (_browser is not null)
        {
            _browser.PropertyChanged -= OnBrowserChanged;
            _browser = null;
        }

        FolderList.Items.Clear();
        FileList.Items.Clear();
        RebuildCrumbs();
    }

    private void OnBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _browser))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(RemoteBrowserViewModel.Crumbs):
                RebuildCrumbs();
                break;
            case nameof(RemoteBrowserViewModel.Entries):
                RebuildBrowseLists();
                break;
            case nameof(RemoteBrowserViewModel.IsBusy):
            case nameof(RemoteBrowserViewModel.Error):
            case nameof(RemoteBrowserViewModel.Summary):
                UpdateBrowseState();
                break;
        }
    }

    private void RebuildBrowseLists()
    {
        // Files that are ticked stay ticked when the listing is ordered or filtered again (and are still shown).
        var ticked = FileList.SelectedItems.OfType<ListViewItem>().Select(i => (string)i.Tag).ToHashSet(StringComparer.Ordinal);
        FolderList.Items.Clear();
        FileList.Items.Clear();
        if (_browser is not { } browser)
        {
            return;
        }

        var keep = new List<ListViewItem>();
        foreach (var entry in browser.Entries)
        {
            if (entry.IsFolder)
            {
                FolderList.Items.Add(FolderRow(entry.Name, entry.Path, "\uE8B7"));
            }
            else
            {
                var row = FileRow(entry);
                FileList.Items.Add(row);
                if (ticked.Contains(entry.Path))
                {
                    keep.Add(row);
                }
            }
        }

        foreach (var row in keep)
        {
            FileList.SelectedItems.Add(row);
        }

        UpdateBrowseState();
    }

    private static ListViewItem FolderRow(string name, string path, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new ListViewItem { Content = row, Tag = path };
        AutomationProperties.SetName(item, name);
        return item;
    }

    private ListViewItem FileRow(RemoteEntry entry)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var size = new TextBlock { Text = FormatSize(entry.Length), Style = (Style)Resources["SecondaryText"], MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 1);
        row.Children.Add(size);
        var when = new TextBlock { Text = entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Style = (Style)Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(when, 2);
        row.Children.Add(when);
        var item = new ListViewItem { Content = row, Tag = entry.Path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, entry.Name);
        return item;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };

    private void UpdateBrowseState()
    {
        if (_browser is not { } browser)
        {
            return;
        }

        BrowseBusy.IsActive = browser.IsBusy;
        BrowseError.Text = browser.Error ?? string.Empty;
        BrowseError.Visibility = Visible(browser.Error is not null);
        NoFoldersText.Visibility = Visible(!browser.IsBusy && browser.Error is null && FolderList.Items.Count == 0);
        NoFilesText.Visibility = Visible(!browser.IsBusy && browser.Error is null && FileList.Items.Count == 0);
        NoFilesText.Text = browser.Filter.Length > 0
            ? "No files match the filter. Change or clear it to see more files."
            : "No matching files here. Change the pattern below to see other files.";
        UpButton.IsEnabled = browser.CanGoUp;
        var selected = FileList.SelectedItems.Count;
        SummaryText.Text = selected > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{selected:N0} selected · {browser.Summary}")
            : browser.Summary;
        UpdateBrowseButtons();
    }

    private void UpdateBrowseButtons()
    {
        if (_stage != Stage.Browse)
        {
            return;
        }

        var selected = FileList.SelectedItems.Count;
        IsPrimaryButtonEnabled = selected >= 1;
        IsSecondaryButtonEnabled = selected >= 2;
    }

    /// <summary>Double-clicking a folder opens it; the first click only selects it, so a second click can't land on a folder of the new listing.</summary>
    private void OnFolderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Only when the double-click is on a row, not on the empty space below the list.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, FolderList); node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem { Tag: string folder } && _browser is { } browser)
            {
                e.Handled = true;
                _ = browser.OpenFolderAsync(folder);
                return;
            }
        }
    }

    private void OnFolderListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && _browser is { } browser && FolderList.SelectedItem is ListViewItem { Tag: string folder })
        {
            e.Handled = true; // don't let Enter press the dialog's default button
            _ = browser.OpenFolderAsync(folder);
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => _ = _browser?.UpAsync();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = _browser?.RefreshAsync();

    // ---------------------------------------------------------------- the folder path

    /// <summary>One button per part of the path, each followed by an arrow listing that folder's sub-folders; parts that don't fit go behind "…".</summary>
    private void RebuildCrumbs()
    {
        _subFolders?.Cancel();
        CrumbPanel.Children.Clear();
        _crumbs.Clear();
        _crumbOverflow = null;
        _hiddenCrumbs = 0;
        if (_browser is not { } browser)
        {
            return;
        }

        var overflow = _crumbOverflow = new Button
        {
            Style = (Style)Resources["CrumbButton"],
            Content = new FontIcon { Glyph = "\uE712", FontSize = 12 },
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(overflow, "Folders further up");
        AutomationProperties.SetName(overflow, "Folders further up");
        var overflowMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        overflowMenu.Opening += (_, _) => FillHiddenCrumbs(overflowMenu);
        overflow.Flyout = overflowMenu;
        CrumbPanel.Children.Add(overflow);

        var crumbs = browser.Crumbs;
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            var next = i + 1 < crumbs.Count ? crumbs[i + 1].Path : null;

            var button = new Button
            {
                Style = (Style)Resources["CrumbButton"],
                Content = new TextBlock { Text = crumb.Name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = crumb.Path,
            };
            button.Click += OnCrumbClick;
            ToolTipService.SetToolTip(button, crumb.Path);
            AutomationProperties.SetName(button, crumb.Name);
            AutomationProperties.SetHelpText(button, crumb.Path);

            var arrow = new Button
            {
                Style = (Style)Resources["CrumbButton"],
                Padding = new Thickness(4, 3, 4, 3),
                Content = new FontIcon { Glyph = "\uE76C", FontSize = 10 },
            };
            var label = string.Create(CultureInfo.InvariantCulture, $"Folders in {crumb.Name}");
            ToolTipService.SetToolTip(arrow, label);
            AutomationProperties.SetName(arrow, label);
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            menu.Opening += (_, _) => FillSubFolders(menu, crumb.Path, next);
            menu.Closed += (_, _) => _subFolders?.Cancel();
            arrow.Flyout = menu;

            CrumbPanel.Children.Add(button);
            CrumbPanel.Children.Add(arrow);
            _crumbs.Add((button, arrow));
        }

        LayoutCrumbs();
    }

    /// <summary>Hides parts from the top down until the rest fits, keeping the current folder and some empty space to click.</summary>
    private void LayoutCrumbs()
    {
        if (_crumbOverflow is not { } overflow || _crumbs.Count == 0 || CrumbBar.ActualWidth <= 0)
        {
            return;
        }

        const double SpaceToClick = 32;
        var available = CrumbBar.ActualWidth - CrumbBar.Padding.Left - CrumbBar.Padding.Right
            - CrumbBar.BorderThickness.Left - CrumbBar.BorderThickness.Right - SpaceToClick;
        var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        var widths = new double[_crumbs.Count];
        for (var i = 0; i < _crumbs.Count; i++)
        {
            var (crumb, arrow) = _crumbs[i];
            crumb.Visibility = arrow.Visibility = Visibility.Visible;
            crumb.Measure(unlimited);
            arrow.Measure(unlimited);
            widths[i] = crumb.DesiredSize.Width + arrow.DesiredSize.Width;
        }

        var total = widths.Sum();
        var hidden = 0;
        if (total > available)
        {
            overflow.Visibility = Visibility.Visible;
            overflow.Measure(unlimited);
            total += overflow.DesiredSize.Width;
            while (hidden < _crumbs.Count - 1 && total > available)
            {
                total -= widths[hidden];
                hidden++;
            }
        }

        for (var i = 0; i < hidden; i++)
        {
            _crumbs[i].Crumb.Visibility = _crumbs[i].Arrow.Visibility = Visibility.Collapsed;
        }

        overflow.Visibility = Visible(hidden > 0);
        _hiddenCrumbs = hidden;
    }

    private void OnCrumbBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            LayoutCrumbs();
        }
    }

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (_browser is { } browser && sender is Button { Tag: string folder })
        {
            _ = browser.OpenFolderAsync(folder);
        }
    }

    /// <summary>The parts behind "…", nearest first (as File Explorer lists them).</summary>
    private void FillHiddenCrumbs(MenuFlyout menu)
    {
        menu.Items.Clear();
        for (var i = _hiddenCrumbs - 1; i >= 0; i--)
        {
            var crumb = _crumbs[i].Crumb;
            menu.Items.Add(FolderMenuItem(((TextBlock)crumb.Content).Text, (string)crumb.Tag, bold: false));
        }
    }

    /// <summary>Lists <paramref name="folder"/>'s sub-folders into <paramref name="menu"/>; <paramref name="next"/> (the part after it) is in bold.</summary>
    private async void FillSubFolders(MenuFlyout menu, string folder, string? next)
    {
        if (_browser is not { } browser)
        {
            return;
        }

        _subFolders?.Cancel();
        var cts = _subFolders = new CancellationTokenSource();
        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem { Text = "Reading the folder…", IsEnabled = false });

        RemoteSubFolders result;
        try
        {
            result = await browser.ListSubFoldersAsync(folder, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return; // the menu closed, or another one opened
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_browser, browser))
        {
            return;
        }

        menu.Items.Clear();
        if (result.Error is not null)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = result.Error, IsEnabled = false });
        }
        else if (result.Folders.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No sub-folders", IsEnabled = false });
        }
        else
        {
            foreach (var entry in result.Folders)
            {
                menu.Items.Add(FolderMenuItem(entry.Name, entry.Path, bold: entry.Path == next));
            }
        }
    }

    private MenuFlyoutItem FolderMenuItem(string name, string path, bool bold)
    {
        var item = new MenuFlyoutItem
        {
            Text = name,
            Tag = path,
            Icon = new FontIcon { Glyph = "\uE8B7" },
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        item.Click += (_, _) => _ = _browser?.OpenFolderAsync(path);
        return item;
    }

    /// <summary>A click on the empty part of the path bar types a path instead, as in File Explorer.</summary>
    private void OnCrumbBarTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CrumbBar) || ReferenceEquals(e.OriginalSource, CrumbPanel))
        {
            e.Handled = true;
            BeginTypingPath();
        }
    }

    private void OnTypePathInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_stage == Stage.Browse)
        {
            args.Handled = true;
            BeginTypingPath();
        }
    }

    private void BeginTypingPath()
    {
        if (_browser is not { } browser)
        {
            return;
        }

        FolderBox.Text = browser.Folder;
        CrumbBar.Visibility = Visibility.Collapsed;
        FolderBox.Visibility = Visibility.Visible;
        FolderBox.Focus(FocusState.Programmatic);
        FolderBox.SelectAll();
    }

    private void EndTypingPath()
    {
        if (FolderBox.Visibility == Visibility.Collapsed)
        {
            return;
        }

        FolderBox.Visibility = Visibility.Collapsed;
        CrumbBar.Visibility = Visibility.Visible;
    }

    private void OnFolderBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when _browser is { } browser:
                e.Handled = true; // don't let Enter press the dialog's default button
                var typed = FolderBox.Text;
                EndTypingPath();
                _ = browser.OpenFolderAsync(typed);
                FocusLastCrumb();
                break;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true; // back to the path, not out of the dialog
                EndTypingPath();
                FocusLastCrumb();
                break;
        }
    }

    private void OnFolderBoxLostFocus(object sender, RoutedEventArgs e) => EndTypingPath();

    private void FocusLastCrumb()
    {
        if (_crumbs.Count > 0)
        {
            _crumbs[^1].Crumb.Focus(FocusState.Programmatic);
        }
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (_browser is { } browser)
        {
            browser.Patterns = PatternBox.Text;
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_browser is { } browser)
        {
            browser.Filter = FilterBox.Text;
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_browser is { } browser && SortBox.SelectedIndex >= 0)
        {
            // Name (A–Z), Name (Z–A), Modified (newest first), Modified (oldest first)
            var index = SortBox.SelectedIndex;
            browser.Sort(index < 2 ? RemoteSort.Name : RemoteSort.Modified, descending: index is 1 or 2);
        }
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBrowseState();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => FileList.SelectAll();

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => FileList.SelectedItems.Clear();
}
