using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using DevTools.Contracts;
using DevTools.Modules.Scoop;

namespace DevTools.App.Views;

public partial class ScoopWorkspaceView : UserControl
{
    private ScoopWorkspace? Workspace => (DataContext as ModulePage)?.ScoopWorkspace;
    private bool initialized;
    private void GridResized(object sender, SizeChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        double[] weights = grid.Columns.Count == 2 ? [3, 1] : grid.Name == "InstalledGrid" ? [2, 1, 1, .6, 1.6] : [1.1, .8, .8, .8, 2.5];
        if (grid.Columns.Count != weights.Length || e.NewSize.Width < 100) return;
        var available = e.NewSize.Width - 22;
        for (var i = 0; i < weights.Length; i++) grid.Columns[i].Width = new DataGridLength(available * weights[i] / weights.Sum());
    }
    public ScoopWorkspaceView() => InitializeComponent();
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        initialized = false;
        if (IsLoaded) LoadedView(sender, new RoutedEventArgs());
    }
    private void Execute(string key) { if (Workspace?.Commands.TryGetValue(key, out var command) == true) command.Execute(null); }
    private void LoadedView(object sender, RoutedEventArgs e)
    {
        if (!initialized && Workspace is not null && (Window.GetWindow(this)?.DataContext is not ShellViewModel shell || shell.AutoRefresh)) { initialized = true; Execute("CheckSetup"); }
    }
    private void InstalledSelection(object sender, SelectionChangedEventArgs e) { if (Workspace is not { } w) return; w.Selection.Clear(); w.Selection.AddRange(((DataGrid)sender).SelectedItems.OfType<ScoopPackage>()); }
    private void SearchSelection(object sender, SelectionChangedEventArgs e) { if (Workspace is not { } w) return; w.SearchSelection.Clear(); w.SearchSelection.AddRange(((DataGrid)sender).SelectedItems.OfType<ScoopPackage>()); }
    private void BucketSelection(object sender, SelectionChangedEventArgs e) { if (Workspace is not { } w) return; w.BucketSelection.Clear(); w.BucketSelection.AddRange(((DataGrid)sender).SelectedItems.OfType<ScoopBucket>()); }
    private void DetailsClick(object sender, MouseButtonEventArgs e) { if (Workspace?.Selected is not null) OpenEditor("PackageEditor", "Package details"); }
    private void OpenDetails(object sender, RoutedEventArgs e) => OpenEditor("PackageEditor", "Package details");
    private void OpenPaths(object sender, RoutedEventArgs e) => OpenEditor("PathEditor", "PATH entries");
    private void OpenEditor(string template, string title)
    {
        if (Workspace?.Selected is not { } selected) { NoticeWindow.Show(title, "Select an installed app first."); return; }
        var dialog = CreateEditor(template, title + " · " + selected.Name);
        dialog.Owner = Window.GetWindow(this); dialog.ShowDialog();
    }
    public ThemedWindow CreateEditor(string template, string title)
    {
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 21, Margin = new Thickness(0, 0, 0, 15) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new ContentControl { Content = Workspace, ContentTemplate = (DataTemplate)FindResource(template) } });
        var dialog = new ThemedWindow { Title = title, Width = 800, Height = 550, MinWidth = 500, MinHeight = 360, Content = panel };
        close.Click += (_, _) => dialog.Close(); return dialog;
    }
    private void OpenActionMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private void ContextOpened(object sender, RoutedEventArgs e) { if (sender is ContextMenu menu) menu.IsEnabled = Workspace?.Selected is not null; }
    private void ContextDetails(object sender, RoutedEventArgs e) => OpenDetails(sender, e);
    private void ContextFolder(object sender, RoutedEventArgs e) => Execute("OpenFolder");
    private void CopyName(object sender, RoutedEventArgs e) { if (Workspace?.Selected is { } a) Clipboard.SetText(a.Name); }
    private void SearchKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Execute("Search"); e.Handled = true; } }
    private void KeyPressed(object sender, KeyEventArgs e) { if (e.Key == Key.F5) { Execute("Refresh"); e.Handled = true; } if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Workspace!.Tab = 0; FilterBox.Focus(); e.Handled = true; } if (e.Key == Key.Escape && Keyboard.FocusedElement == FilterBox) Workspace!.Filter = ""; }
}

