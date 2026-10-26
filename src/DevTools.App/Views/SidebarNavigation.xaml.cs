using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevTools.App.Views;

public partial class SidebarNavigation : UserControl
{
    public SidebarNavigation() => InitializeComponent();
    private void ToggleGroup(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavigationNode { IsGroup: true } node }) return;
        // Consume the header press before TreeViewItem's default double-click handling.
        if (e.ClickCount <= 1) node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }
    private void Navigate(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavigationNode { Page: { } page } && DataContext is ShellViewModel shell && shell.Selected.Id != page.Id)
            shell.Selected = shell.Pages.FirstOrDefault(p => p.Id == page.Id) ?? page;
    }
}
