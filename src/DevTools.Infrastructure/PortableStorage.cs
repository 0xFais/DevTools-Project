using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DevTools.Infrastructure;

/// <summary>App-owned data stays beside the executable; system ownership records stay with their PC/user.</summary>
public sealed class PortableStorage
{
    public string AppDirectory { get; }
    public string DataRoot { get; }
    public string MachineDirectory { get; }
    public bool ImportedLegacyData { get; private set; }
    public PortableStorage(string appDirectory, string machineIdentity, string? legacyDirectory = null)
    {
        AppDirectory = Path.GetFullPath(appDirectory);
        DataRoot = Path.Combine(AppDirectory, "Data");
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machineIdentity))).ToLowerInvariant()[..24];
        MachineDirectory = Path.Combine(DataRoot, "Machines", profile);
        var ready = Path.Combine(MachineDirectory, "portable-profile.json");
        var pending = Path.Combine(MachineDirectory, "legacy-import.pending");
        Directory.CreateDirectory(MachineDirectory);
        // Never silently fall back to AppData when the portable drive is read-only.
        var probe = Path.Combine(DataRoot, ".write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        if (!File.Exists(ready))
        {
            // An existing shared portable profile belongs to this copy, not the next PC's legacy installation.
            if ((!File.Exists(Path.Combine(DataRoot, "settings.json")) || File.Exists(pending)) && legacyDirectory is not null && Directory.Exists(legacyDirectory))
            {
                File.WriteAllText(pending, "Importing legacy data without deleting the original files.");
                CopyLegacy(legacyDirectory, MachineDirectory);
                var old = Path.Combine(legacyDirectory, "settings.json");
                if (File.Exists(old))
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(old)) ?? [];
                    var shared = new SettingsStore(DataRoot); var machine = new SettingsStore(MachineDirectory);
                    foreach (var pair in values) (SharedSetting(pair.Key) ? shared : machine).Save(pair.Key, SharedSetting(pair.Key) ? EncodePath(pair.Key, pair.Value) : pair.Value);
                }
                ImportedLegacyData = true;
            }
            File.WriteAllText(ready, JsonSerializer.Serialize(new { Format = 1, ImportedLegacyData }));
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
    public static PortableStorage OpenDefault() => new(AppContext.BaseDirectory, CurrentMachineIdentity(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolsStudio"));
    public static string CurrentMachineIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        using var identity = WindowsIdentity.GetCurrent();
        return (key?.GetValue("MachineGuid")?.ToString() ?? Environment.MachineName) + ":" + identity.User!.Value;
    }
    public static bool SharedSetting(string key) => key is "settings.theme" or "scoop.root"
        || key.StartsWith("navigation.", StringComparison.Ordinal) || key.StartsWith("extensions.enabled.", StringComparison.Ordinal)
        || key.StartsWith("context.path.", StringComparison.Ordinal) || key.StartsWith("links.path.", StringComparison.Ordinal);
    private static bool PathSetting(string key) => key is "scoop.root" || key.StartsWith("context.path.", StringComparison.Ordinal) || key.StartsWith("links.path.", StringComparison.Ordinal);
    public string EncodePath(string key, string value)
    {
        if (!PathSetting(key) || !Path.IsPathFullyQualified(value)) return value;
        var relative = Path.GetRelativePath(AppDirectory, value);
        return IsInside(relative) ? "@portable/" + relative.Replace('\\', '/') : value;
    }
    public string DecodePath(string key, string value)
    {
        if (!PathSetting(key) || !value.StartsWith("@portable/", StringComparison.Ordinal)) return value;
        var relative = value[10..].Replace('/', Path.DirectorySeparatorChar);
        if (!IsInside(relative)) throw new InvalidDataException("A portable path points outside the app folder.");
        var full = Path.GetFullPath(Path.Combine(AppDirectory, relative));
        if (!IsInside(Path.GetRelativePath(AppDirectory, full))) throw new InvalidDataException("Invalid portable path.");
        return full;
    }
    private static bool IsInside(string relative) => !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static void CopyLegacy(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var name = Path.GetFileName(entry);
            if (name is "UpdateRecovery" or "settings.json" || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0) { Directory.CreateDirectory(target); CopyLegacy(entry, target); }
            else if (!File.Exists(target)) File.Copy(entry, target);
        }
    }
}
