using LogLens.App.Views;
using LogLens.Core.Remote;
using LogLens.Infrastructure.Remote;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace LogLens.App;

/// <summary>Remote hosts (FR-O7): File ▸ Open from remote host, and what the SSH questions asked outside it are shown over.</summary>
public sealed partial class MainWindow
{
    private string _remotePatterns = FilePatterns.Default;

    /// <summary>Where the SSH questions asked outside the remote-host dialog (recent files, the session, the command line) are shown.</summary>
    internal XamlRoot DialogRoot => Root.XamlRoot;

    internal ElementTheme DialogTheme => Root.ActualTheme;

    /// <summary>The hosts in the user's <c>~/.ssh/config</c>; none when there is no such file.</summary>
    internal static IReadOnlyList<RemoteHost> SshConfigHosts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return SshConfig.ReadHosts(SshConfig.DefaultPath(home), home, Environment.UserName);
    }

    private async void OnOpenRemote(object sender, RoutedEventArgs e) => await OpenRemoteAsync();

    private async void OnEmptyStateOpenRemote(object? sender, EventArgs e) => await OpenRemoteAsync();

    /// <summary>Choose a host, browse its folders, then open the chosen files in tabs or merge them.</summary>
    private async Task OpenRemoteAsync()
    {
        var dialog = new RemoteDialog(
            _settings.Current.RemoteHosts,
            SshConfigHosts(),
            _remote,
            _hostKeys,
            hosts => _settings.Update(s => s with { RemoteHosts = hosts }),
            _remotePatterns,
            PickKeyFileAsync)
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };

        await dialog.ShowAsync();
        _remotePatterns = dialog.Patterns;
        var selected = dialog.SelectedPaths;
        switch (dialog.Choice)
        {
            case RemoteDialog.Result.Tabs when selected.Count > 0:
                await ViewModel.OpenAsync(selected);
                break;
            case RemoteDialog.Result.Merge when selected.Count > 0:
                await ViewModel.OpenMergedAsync(selected);
                break;
        }
    }

    private async Task<string?> PickKeyFileAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
