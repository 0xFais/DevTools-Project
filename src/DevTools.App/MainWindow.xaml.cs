using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevTools.Contracts;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DevTools.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<PasswordBox, (ModuleField Field, PropertyChangedEventHandler Handler)> secretBindings = [];
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Custom chrome must maximize to this monitor's work area, in native pixels.
        // Otherwise Windows can place the footer under the taskbar or off screen.
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return 0;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        bounds.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ModuleField field } box)
            field.Value = box.Password;
    }
    private void SecretLoaded(object sender, RoutedEventArgs e) => BindSecret((PasswordBox)sender);
    private void SecretUnloaded(object sender, RoutedEventArgs e) => UnbindSecret((PasswordBox)sender);
    private void SecretContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (PasswordBox)sender;
        UnbindSecret(box);
        if (box.IsLoaded)
            BindSecret(box);
    }
    private void BindSecret(PasswordBox box)
    {
        UnbindSecret(box);
        if (box.DataContext is not ModuleField { Kind: FieldKind.Secret } field)
            return;
        box.Password = field.Value;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ModuleField.Value) && box.Password != field.Value)
                box.Password = field.Value;
        };
        secretBindings[box] = (field, handler);
        field.PropertyChanged += handler;
    }
    private void UnbindSecret(PasswordBox box)
    {
        if (secretBindings.Remove(box, out var binding))
            binding.Field.PropertyChanged -= binding.Handler;
    }
    private void LogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.ScrollToEnd();
    }
}
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true; public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
