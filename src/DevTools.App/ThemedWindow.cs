using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevTools.App;

/// <summary>Explicitly applies shared resources to derived windows and their native title bars.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        SetResourceReference(StyleProperty, typeof(Window));
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ApplyCaption();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty || e.Property == ForegroundProperty) ApplyCaption();
    }
    private void ApplyCaption()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || Background is not SolidColorBrush bg || Foreground is not SolidColorBrush fg) return;
        var dark = bg.Color.R + bg.Color.G + bg.Color.B < 384 ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
        var caption = ColorRef(bg.Color); var text = ColorRef(fg.Color);
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    public static Window? ActiveOwner => Application.Current?.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}

public sealed class NoticeWindow : ThemedWindow
{
    public NoticeWindow(string title, string message, bool confirm = false)
    {
        Title = title; Width = 660; Height = 440; MinWidth = 400; MinHeight = 260;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 21, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var yes = new Button { Content = confirm ? "Continue" : "OK", IsDefault = true, MinWidth = 90 };
        yes.Click += (_, _) => DialogResult = true; buttons.Children.Add(yes);
        if (confirm) { var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) }; buttons.Children.Add(cancel); }
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        Content = panel;
    }
    public static bool Show(string title, string message, bool confirm = false)
    {
        var dialog = new NoticeWindow(title, message, confirm);
        var owner = ActiveOwner; if (owner?.IsLoaded == true) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }
}
