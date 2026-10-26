using DevTools.Contracts;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed class WindowsUpdateServices(IModuleContext context) : IWindowsUpdateServices
{
    public UpdateServiceState[] Read() => UpdateController.Names.Select(ReadService).ToArray();
    private static UpdateServiceState ReadService(string name)
    {
        // Read configuration and status through the same service manager used by sc.exe.
        var manager = OpenSCManager(null, null, 1);
        if (manager == 0) return new(name, -1, 0);
        try
        {
            var service = OpenService(manager, name, 1 | 4);
            if (service == 0) return new(name, -1, 0);
            try
            {
                var state = QueryServiceStatus(service, out var status) ? (int)status.CurrentState : 0;
                QueryServiceConfig(service, 0, 0, out var needed);
                if (needed == 0 || needed > 8192) return new(name, -1, state);
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    return new(name, QueryServiceConfig(service, buffer, needed, out _) ? (int)Marshal.PtrToStructure<ServiceConfiguration>(buffer).StartType : -1, state);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static int ReadState(string name)
    {
        var manager = OpenSCManager(null, null, 1); if (manager == 0) return 0;
        try { var service = OpenService(manager, name, 4); if (service == 0) return 0; try { return QueryServiceStatus(service, out var status) ? (int)status.CurrentState : 0; } finally { CloseServiceHandle(service); } }
        finally { CloseServiceHandle(manager); }
    }
    private static void CheckName(string name) { if (!UpdateController.Names.Contains(name)) throw new ArgumentException("Unsupported service."); }
    public async Task SetStartAsync(string service, int startType, CancellationToken token)
    {
        CheckName(service); var mode = startType switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => throw new ArgumentException("Unsupported startup type.") };
        (await context.RunAsync("sc.exe", ["config", service, "start=", mode], token)).EnsureSuccess();
    }
    public async Task StopAsync(string service, CancellationToken token)
    {
        CheckName(service); if (ReadState(service) == 1) return;
        var result = await context.RunAsync("sc.exe", ["stop", service], token);
        if (result.ExitCode is not (0 or 1062)) result.EnsureSuccess();
        await WaitState(service, 1, token);
    }
    public async Task StartAsync(string service, CancellationToken token)
    {
        CheckName(service); if (ReadState(service) == 4) return;
        var result = await context.RunAsync("sc.exe", ["start", service], token);
        if (result.ExitCode is not (0 or 1056)) result.EnsureSuccess();
        await WaitState(service, 4, token);
    }
    private static async Task WaitState(string service, int state, CancellationToken token)
    {
        for (var i = 0; i < 60; i++) { if (ReadState(service) == state) return; await Task.Delay(500, token); }
        throw new TimeoutException("The service did not reach the expected state: " + service);
    }
    public async Task<string> CheckHealthAsync(CancellationToken token)
    {
        const string script = "try { $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $session=New-Object -ComObject Microsoft.Update.Session; $searcher=$session.CreateUpdateSearcher(); $r=$searcher.Search(\"IsInstalled=0 and IsHidden=0 and Type='Software'\"); @{Code=[int]$r.ResultCode; Count=$r.Updates.Count} | ConvertTo-Json -Compress } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await context.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", encoded], token);
        using var doc = JsonDocument.Parse(result.EnsureStandardOutputSuccess()); var code = doc.RootElement.GetProperty("Code").GetInt32();
        if (code is not (2 or 3)) throw new InvalidOperationException("Windows Update search did not succeed. Result code: " + code);
        return (code == 2 ? "Check succeeded" : "Check completed with warnings") + " · " + doc.RootElement.GetProperty("Count").GetInt32() + " available updates. No download/install requested.";
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfiguration { public uint ServiceType, StartType, ErrorControl; public nint BinaryPath, LoadOrderGroup; public uint TagId; public nint Dependencies, ServiceStartName, DisplayName; }
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(nint service, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
}
