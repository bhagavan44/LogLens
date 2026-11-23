using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Asks what "Find all events containing" (FR-N4) looks for, and with how much context.</summary>
public sealed partial class FindAllDialog : ContentDialog
{
    public FindAllDialog(string text, int contextEvents, bool merged)
    {
        InitializeComponent();
        InputBox.Text = text ?? string.Empty;
        ContextBox.Value = contextEvents;
        HintText.Text = merged
            ? "This view merges several files, so the search covers all of them. Text is matched in any case, in messages and exceptions."
            : "Text is matched in any case, in messages and exceptions. Tip: select text in the detail pane first and it is filled in here.";
        Opened += (_, _) =>
        {
            InputBox.Focus(FocusState.Programmatic);
            InputBox.SelectAll();
        };
    }

    public string Text => InputBox.Text;

    public int ContextEvents => double.IsNaN(ContextBox.Value) ? 0 : (int)ContextBox.Value;

    public bool ClearOtherFilters => ClearBox.IsChecked == true;
}
