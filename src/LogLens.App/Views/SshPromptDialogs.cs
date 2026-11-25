using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using LogLens.Infrastructure.Remote;

namespace LogLens.App.Views;

/// <summary>
/// The SSH questions (password, passphrase, unknown host key) as small dialogs over the main window: used when a remote file is opened
/// from Open recent, the restored session or the command line. The remote-host dialog asks the same questions inline instead.
/// </summary>
public sealed class SshPromptDialogs(Func<XamlRoot> root, Func<ElementTheme> theme) : ISshPrompt
{
    public Task<string?> AskPasswordAsync(SshTarget target, string? problem, CancellationToken cancellationToken)
        => AskSecretAsync(
            $"Sign in to {target.DisplayName}",
            $"Enter the password of {target.UserName} on {target.Host}. It is kept in memory only, until LogLens closes.",
            problem,
            "Connect",
            cancellationToken);

    public Task<string?> AskPassphraseAsync(SshTarget target, string keyFile, string? problem, CancellationToken cancellationToken)
        => AskSecretAsync(
            "Key passphrase",
            $"The key {keyFile} is protected by a passphrase. Enter it to sign in to {target.Host}, or skip this key.",
            problem,
            "Unlock",
            cancellationToken);

    public async Task<bool> ConfirmHostKeyAsync(SshTarget target, HostKeyInfo key, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{target.Host} hasn't been used before: its host key isn't in your known_hosts file and wasn't accepted in LogLens. " +
                   "Check that this fingerprint matches the server's before trusting it.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = $"{key.KeyType}\n{key.Fingerprint}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });

        var dialog = NewDialog("Unknown host", panel, "Trust and connect");
        using var registration = cancellationToken.Register(dialog.Hide);
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellationToken.IsCancellationRequested;
    }

    private async Task<string?> AskSecretAsync(string title, string message, string? problem, string primary, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 480 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var box = new PasswordBox();
        AutomationProperties.SetName(box, title);
        panel.Children.Add(box);
        if (problem is not null)
        {
            panel.Children.Add(new TextBlock { Text = problem, Style = (Style)Application.Current.Resources["ErrorTextStyle"] });
        }

        var dialog = NewDialog(title, panel, primary);
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        using var registration = cancellationToken.Register(dialog.Hide);
        var result = await dialog.ShowAsync();
        var secret = box.Password;
        box.Password = string.Empty;
        return result == ContentDialogResult.Primary && !cancellationToken.IsCancellationRequested ? secret : null;
    }

    private ContentDialog NewDialog(string title, UIElement content, string primary) => new()
    {
        XamlRoot = root(),
        RequestedTheme = theme(),
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };
}
