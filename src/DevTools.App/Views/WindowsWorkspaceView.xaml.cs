using System.Windows;
using System.Windows.Controls;
using DevTools.Contracts;

namespace DevTools.App.Views;

public partial class WindowsWorkspaceView : UserControl
{
    public WindowsWorkspaceView() { InitializeComponent(); PasswordBinding.Attach(CustomKeyBox); DataContextChanged += (_, _) => ApplyKind(); Loaded += (_, _) => ApplyKind(); }
    private void ApplyKind()
    {
        var activation = DataContext is ModulePage { WindowsWorkspace.Kind: "Activation" };
        UpdateFacts.Visibility = UpdateStatusActions.Visibility = UpdateOptions.Visibility = ServiceDetailsButton.Visibility = activation ? Visibility.Collapsed : Visibility.Visible;
        ActivationStatusActions.Visibility = ActivationOptions.Visibility = activation ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CopyKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModulePage { WindowsWorkspace: { HasPublicKey: true } workspace }) Clipboard.SetText(workspace.PublicKey);
    }
}
