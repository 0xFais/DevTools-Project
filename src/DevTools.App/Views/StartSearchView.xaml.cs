using System.Windows;
using System.Windows.Controls;
using DevTools.Contracts;

namespace DevTools.App.Views;

public partial class StartSearchView : UserControl
{
    public StartSearchView() => InitializeComponent();
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (IsLoaded) LoadedView(sender, new RoutedEventArgs()); }
    private void LoadedView(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel { AutoRefresh: false }) return;
        if ((DataContext as ModulePage)?.StartSearchWorkspace is { IsInitialized: false, CheckAction: { } action } && action.CanExecute(null)) action.Execute(null);
    }
}
