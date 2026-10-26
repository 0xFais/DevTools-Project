using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DevTools.App;

namespace DevTools.Tests;

internal static class WindowBoundsTests
{
    internal static void RunAll(ShellViewModel shell, Action<string, Action> run)
    {
        run("Maximized custom window keeps the footer inside the monitor work area and restores", () =>
        {
            var window = new MainWindow { DataContext = shell, Width = 1100, Height = 760 };
            try
            {
                window.Show();
                window.WindowState = WindowState.Maximized;
                Drain(window);
                var hwnd = new WindowInteropHelper(window).Handle;
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) throw new Exception("Monitor work area unavailable.");
                var footer = (FrameworkElement)window.FindName("WorkspaceFooter");
                var top = footer.PointToScreen(new Point(0, 0));
                var bottom = footer.PointToScreen(new Point(footer.ActualWidth, footer.ActualHeight));
                if (footer.ActualHeight < 29 || top.Y < monitor.Work.Top - 1 || bottom.Y > monitor.Work.Bottom + 1 || top.X < monitor.Work.Left - 1 || bottom.X > monitor.Work.Right + 1)
                    throw new Exception($"Footer outside work area: {top} to {bottom}; work area {monitor.Work.Left},{monitor.Work.Top},{monitor.Work.Right},{monitor.Work.Bottom}.");
                window.WindowState = WindowState.Normal;
                Drain(window);
                if (Math.Abs(window.ActualWidth - 1100) > 2 || Math.Abs(window.ActualHeight - 760) > 2)
                    throw new Exception("Restoring changed the normal window size.");
            }
            finally { window.Close(); }
        });
    }
    private static void Drain(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
