using DevTools.Contracts;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Windows;

public sealed record WindowsLicense(string Status, bool Activated, string Details);
public interface IWindowsActivationBackend
{
    WindowsEdition ReadEdition();
    Task<WindowsLicense> ReadLicenseAsync(CancellationToken token);
    Task<string> DetailsAsync(CancellationToken token);
    Task<string> ActivateAsync(string key, string server, bool volumeKey, CancellationToken token);
}
public sealed class WindowsActivationBackend(IModuleContext context) : IWindowsActivationBackend
{
    public WindowsEdition ReadEdition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var id = key?.GetValue("EditionID")?.ToString() ?? "Unknown";
        int.TryParse(key?.GetValue("CurrentBuildNumber")?.ToString(), out var build);
        return new(id, build, key?.GetValue("ProductName")?.ToString() ?? "Windows", key?.GetValue("InstallationType")?.ToString() ?? "Client", key?.GetValue("UBR") is int revision ? revision : 0);
    }
    public async Task<WindowsLicense> ReadLicenseAsync(CancellationToken token)
    {
        const string script = "try { $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $rows=@(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter \"ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL\" | Where-Object {$_.LicenseIsAddon -ne $true} | Select-Object Name,Description,LicenseStatus,PartialProductKey); ConvertTo-Json -InputObject $rows -Compress } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await context.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", encoded], token);
        using var doc = JsonDocument.Parse(result.EnsureStandardOutputSuccess());
        var rows = doc.RootElement.EnumerateArray().ToArray();
        if (rows.Length == 0) return new("No installed license detected", false, "Windows did not return a licensing product with a partial key. Check Windows activation settings.");
        var selected = rows.FirstOrDefault(e => e.GetProperty("LicenseStatus").GetInt32() == 1); if (selected.ValueKind == JsonValueKind.Undefined) selected = rows[0];
        var code = selected.GetProperty("LicenseStatus").GetInt32();
        var label = code switch { 0 => "Not activated", 1 => "Activated", 2 => "Initial grace period", 3 => "Out-of-tolerance grace", 4 => "Non-genuine grace", 5 => "Notification mode", 6 => "Extended grace", _ => "Unknown" };
        return new(label, code == 1, selected.GetProperty("Name").ToString() + "\n" + selected.GetProperty("Description").ToString() + "\nLicense status: " + label + "\nInstalled key ending: " + selected.GetProperty("PartialProductKey").ToString());
    }
    private static string Script => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\slmgr.vbs");
    public async Task<string> DetailsAsync(CancellationToken token) => (await context.RunAsync("cscript.exe", ["//Nologo", Script, "/dlv"], token)).EnsureSuccess();
    public static void ValidateInput(string key, string server)
    {
        if (!Regex.IsMatch(key, "^[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4}$")) throw new ArgumentException("Invalid product-key format.");
        if (server != "" && (!Regex.IsMatch(server, "^[A-Za-z0-9.-]+(?::[0-9]{1,5})?$") || server.Contains(':') && (!int.TryParse(server.Split(':')[1], out var port) || port is < 1 or > 65535))) throw new ArgumentException("Invalid organization server hostname or port.");
    }
    public async Task<string> ActivateAsync(string key, string server, bool volumeKey, CancellationToken token)
    {
        ValidateInput(key, server);
        var installed = await context.RunAsync("cscript.exe", ["//Nologo", Script, "/ipk", key], token);
        if (installed.ExitCode != 0) throw new InvalidOperationException("Windows could not install the selected key. Exit code: " + installed.ExitCode);
        if (volumeKey || server != "") (await context.RunAsync("cscript.exe", server == "" ? ["//Nologo", Script, "/ckms"] : ["//Nologo", Script, "/skms", server], token)).EnsureSuccess();
        return (await context.RunAsync("cscript.exe", ["//Nologo", Script, "/ato"], token)).EnsureSuccess();
    }
}
