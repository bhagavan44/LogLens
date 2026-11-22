using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App.Views;

/// <summary>Shown when no file is open (UX-2): the drop zone and an Open button.</summary>
public sealed partial class EmptyStateView : UserControl
{
    public EmptyStateView()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenRequested;

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
}
