using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevTools.App;

public sealed class ReportWindow : ThemedWindow
{
    public ReportWindow(string title, string content)
    {
        Title = title;
        Width = 860;
        Height = 600;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 22, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var copy = new Button { Content = "Copy report", Margin = new Thickness(0, 0, 10, 0) };
        copy.Click += (_, _) => Clipboard.SetText(content);
        buttons.Children.Add(copy);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 });
        Content = panel;
    }
}
