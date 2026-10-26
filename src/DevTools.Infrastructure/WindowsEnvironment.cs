using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DevTools.Infrastructure;

public static partial class WindowsEnvironment
{
    public static string ContextCommand(string id, string executable, string scope)
    {
        if (executable.Contains('"'))
            throw new ArgumentException("Invalid executable path.");
        var arg = scope == "background" ? "%V" : "%1";
        var prefix = id switch
        {
            "terminal" => " -d",
            "pwsh" => " -NoExit -WorkingDirectory",
            "gitbash" => " --cd=",
            "wezterm" => " start --cwd",
            "alacritty" => " --working-directory",
            _ => ""
        };
        return prefix.EndsWith('=') ? $"\"{executable}\"{prefix}\"{arg}\"" : $"\"{executable}\"{prefix} \"{arg}\"";
    }
    public static void Broadcast() => SendMessageTimeout(new nint(0xffff), 0x1a, 0, "Environment", 2, 5000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
