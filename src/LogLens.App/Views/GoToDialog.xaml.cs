using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Asks for an event number or a time (FR-G7). The input is resolved by the view-model; the dialog stays open on an error.</summary>
public sealed partial class GoToDialog : ContentDialog
{
    private readonly Func<string, (int Row, string? Error)> _resolve;

    public GoToDialog(Func<string, (int Row, string? Error)> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        InitializeComponent();
        Opened += (_, _) => InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>The row to reveal once the dialog closed with Go; -1 otherwise.</summary>
    public int TargetRow { get; private set; } = -1;

    private void OnGoClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var (row, error) = _resolve(InputBox.Text);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true; // keep the dialog open so the text can be corrected
            return;
        }

        TargetRow = row;
    }
}
