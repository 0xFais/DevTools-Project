using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DevTools.Modules.Scoop;

namespace DevTools.App.Views;

public sealed class ScoopIconConverter : IValueConverter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> cache = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string directory || !Directory.Exists(directory)) return null;
        try
        {
            var current = ScoopService.Current(directory);
            var stamp = File.GetLastWriteTimeUtc(ScoopService.MetadataPath(current, "manifest.json")).Ticks;
            var key = current + "|" + stamp + "|" + Directory.GetLastWriteTimeUtc(current).Ticks;
            if (cache.TryGetValue(key, out var cached)) return cached;
            var candidates = ScoopService.IconCandidates(directory);
            // Prefer actual executables, including ones nested in bin or app folders.
            foreach (var file in candidates.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                if (Extract(file) is { } icon) { cache[key] = icon; return icon; }
            foreach (var file in candidates.Where(f => f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var stream = File.OpenRead(file); var icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); icon.Freeze(); cache[key] = icon; return icon; }
                catch (Exception e) when (e is IOException or NotSupportedException or System.IO.FileFormatException) { }
            }
            // Script-only apps use their registered file-type icon. Do not cache misses.
            foreach (var file in candidates) if (ShellIcon(file) is { } icon) return icon;
            return Extract(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"), 2);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return null; }
    }
    private static BitmapSource FromHandle(nint handle)
    {
        try { var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(18, 18)); image.Freeze(); return image; }
        finally { DestroyIcon(handle); }
    }
    private static BitmapSource? Extract(string file, int index = 0)
    {
        var handles = new nint[1];
        return ExtractIconEx(file, index, null, handles, 1) > 0 && handles[0] != 0 ? FromHandle(handles[0]) : null;
    }
    private static BitmapSource? ShellIcon(string file)
    {
        var result = SHGetFileInfo(file, 0, out var info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100 | 0x1);
        return result != 0 && info.Icon != 0 ? FromHandle(info.Icon) : null;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo
    {
        public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, out ShellInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[] small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}

// Resolve shell and executable icons away from the dispatcher. A recycled row only
// receives its own result, and concurrency is bounded during a full list refresh.
public static class ScoopAsyncIcon
{
    private static readonly SemaphoreSlim Workers = new(2);
    private static readonly ScoopIconConverter Converter = new();
    public static readonly DependencyProperty DirectoryProperty = DependencyProperty.RegisterAttached(
        "Directory", typeof(string), typeof(ScoopAsyncIcon), new PropertyMetadata(null, Changed));
    public static string? GetDirectory(DependencyObject target) => (string?)target.GetValue(DirectoryProperty);
    public static void SetDirectory(DependencyObject target, string? value) => target.SetValue(DirectoryProperty, value);
    private static async void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not System.Windows.Controls.Image image) return;
        image.Source = null;
        if (args.NewValue is not string directory || string.IsNullOrWhiteSpace(directory)) return;
        BitmapSource? result = null;
        try
        {
            result = await Task.Run(async () =>
            {
                await Workers.WaitAsync().ConfigureAwait(false);
                try { return Converter.Convert(directory, typeof(BitmapSource), null!, CultureInfo.InvariantCulture) as BitmapSource; }
                finally { Workers.Release(); }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        await image.Dispatcher.InvokeAsync(() =>
        {
            if (GetDirectory(image) == directory) image.Source = result;
        });
    }
}
