using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevTools.App;
using DevTools.App.Views;

namespace DevTools.Tests;

internal static class PopupTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Popup regression assertion failed."); }
    internal static void RunAll(string artifacts, ShellViewModel shell, Action<string, Action> run)
    {
        run("Derived popups inherit Studio background and foreground in dark and light modes", () =>
        {
            var context = new AppModuleContext(Path.Combine(artifacts, "popup-theme"));
            using var previewShell = new ShellViewModel(context, autoRefresh: false);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                context.Save("settings.theme", theme); previewShell.ApplyTheme();
                var report = new ReportWindow("Package details · alpha", "alpha · 2.0\nA portable application\nBucket: main\nLicense: MIT\nShims: bin/alpha.exe");
                var notice = new NoticeWindow("Confirm package operation", "Apply this change to the selected packages?", confirm: true);
                var picker = new ThemedFilePicker("Choose an application folder", true, initialDirectory: artifacts);
                foreach (var popup in new ThemedWindow[] { report, notice, picker })
                {
                    Check(((SolidColorBrush)popup.Background).Color == ((SolidColorBrush)Application.Current.Resources["AppBackground"]).Color);
                    Check(((SolidColorBrush)popup.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["TextBrush"]).Color);
                    Render(popup, Path.Combine(artifacts, popup.GetType().Name + "-" + theme.ToLowerInvariant() + ".png"));
                    if (popup is ReportWindow) Check(Children((DependencyObject)popup.Content).OfType<TextBox>().Single().VerticalContentAlignment == VerticalAlignment.Top);
                    popup.Close();
                }
            }
            context.Save("settings.theme", "Dark"); previewShell.ApplyTheme(); shell.ApplyTheme();
        });
        run("Scoop has a taller list and dedicated package and PATH dialog buttons", () =>
        {
            var page = ScoopTests.Preview(artifacts); page.ScoopWorkspace!.Selected = page.ScoopWorkspace.Installed.First();
            var view = new ScoopWorkspaceView { DataContext = page };
            view.Measure(new Size(900, 1000)); view.Arrange(new Rect(0, 0, 900, 1000)); view.UpdateLayout();
            var elements = Children(view).ToArray();
            Check(elements.OfType<DataGrid>().Single(g => g.Name == "InstalledGrid").ActualHeight >= 400);
            Check(elements.OfType<Button>().Any(b => Equals(b.Content, "Package details…")) && elements.OfType<Button>().Any(b => Equals(b.Content, "Add / edit PATH…")));
            Check(!elements.OfType<Expander>().Any());
            var reset = elements.OfType<Button>().Single(b => Equals(b.Content, "Reset selected"));
            Check(ReferenceEquals(reset.Command, page.ScoopWorkspace.Commands["reset"]));
            Check(!elements.OfType<Button>().Any(b => Equals(b.Content, "Status")));
            var maintenance = elements.OfType<Button>().Single(b => Equals(b.Content, "Maintenance ▾"));
            var menuHost = new ThemedWindow { Content = view, Width = 1050, Height = 800 };
            menuHost.Show(); menuHost.UpdateLayout();
            maintenance.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(maintenance.ContextMenu!.IsOpen && ReferenceEquals(maintenance.ContextMenu.DataContext, page.ScoopWorkspace));
            Check(maintenance.ContextMenu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, page.ScoopWorkspace.Commands["ResetAll"])));
            maintenance.ContextMenu.IsOpen = false;
            menuHost.Close();
            foreach (var key in new[] { "PackageEditor", "PathEditor" })
            {
                var dialog = view.CreateEditor(key, key == "PathEditor" ? "PATH entries · alpha" : "Package details · alpha");
                Render(dialog, Path.Combine(artifacts, key + "-dark.png"));
                var buttons = Children((DependencyObject)dialog.Content).OfType<Button>().ToArray();
                Check(buttons.Any(b => Equals(b.Content, key == "PathEditor" ? "Save rules" : "Switch version")));
                dialog.Close();
            }
        });
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Children(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Render(Window window, string path)
    {
        var content = (UIElement)window.Content; window.Content = null;
        var root = new Border { Background = window.Background, Child = content };
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        const int width = 820, height = 540;
        for (var i = 0; i < 2; i++) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
        root.Child = null; window.Content = content;
    }
}
