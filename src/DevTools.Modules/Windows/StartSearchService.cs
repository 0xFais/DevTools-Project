using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;
using DevTools.Infrastructure;
using Microsoft.Win32;

namespace DevTools.Modules.Windows;

public sealed record StartSearchValue(RegistryValueKind Kind, string Json)
{
    public static StartSearchValue Dword(int value) => new(RegistryValueKind.DWord, JsonSerializer.Serialize(value));
}
public sealed record StartSearchKey(string Id, string Path, string Name, bool Recommendations, bool Machine = false);
public sealed record StartSearchPlatform(WindowsEdition Windows, bool IsAdministrator);
public interface IStartSearchBackend
{
    StartSearchPlatform Platform();
    StartSearchValue? Read(StartSearchKey key, bool machine = false);
    void Write(StartSearchKey key, StartSearchValue? value);
    void Notify();
    Task RestartExplorerAsync(CancellationToken ct);
}
public sealed class StartSearchRegistryBackend : IStartSearchBackend
{
    public StartSearchPlatform Platform()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int.TryParse(key?.GetValue("CurrentBuildNumber")?.ToString(), out var build);
        var edition = new WindowsEdition(key?.GetValue("EditionID")?.ToString() ?? "Unknown", build, key?.GetValue("ProductName")?.ToString() ?? "Windows", key?.GetValue("InstallationType")?.ToString() ?? "Client", key?.GetValue("UBR") is int revision ? revision : 0);
        using var identity = WindowsIdentity.GetCurrent();
        return new(edition, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
    }
    public StartSearchValue? Read(StartSearchKey setting, bool machine = false)
    {
        using var key = (machine || setting.Machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(setting.Path);
        var value = key?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : new(key!.GetValueKind(setting.Name), JsonSerializer.Serialize(value, value.GetType()));
    }
    public void Write(StartSearchKey setting, StartSearchValue? value)
    {
        var hive = setting.Machine ? Registry.LocalMachine : Registry.CurrentUser;
        if (value is null) { using var existing = hive.OpenSubKey(setting.Path, true); existing?.DeleteValue(setting.Name, false); return; }
        object raw = value.Kind switch
        {
            RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(value.Json),
            RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(value.Json),
            RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(value.Json)!,
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Json)!,
            RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(value.Json)!,
            _ => throw new InvalidDataException("Unsupported saved registry value type.")
        };
        using var key = hive.CreateSubKey(setting.Path);
        key.SetValue(setting.Name, raw, value.Kind);
    }
    public void Notify() => WindowsEnvironment.Broadcast();
    public Task RestartExplorerAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var session = Process.GetCurrentProcess().SessionId;
        bool Running()
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
                using (process) { try { if (process.SessionId == session && !process.HasExited) return true; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
            return false;
        }
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
                using (process)
                {
                    int processSession;
                    try { processSession = process.SessionId; }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    if (processSession != session) continue;
                    try { process.Kill(); if (!process.WaitForExit(5000)) throw new IOException("Explorer did not stop within five seconds."); }
                    catch (InvalidOperationException) { }
                }
        }
        finally
        {
            // Once stopped, restore this session's shell even if cancellation arrives.
            if (!Running()) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        }
    }, ct);
}

public sealed record StartSearchSnapshot(string Platform, bool CanBing, bool CanRecommendations, bool BingDisabled, bool RecommendationsDisabled, string BingState, string RecommendationsState, string BingHint, string RecommendationsHint, bool HasBackup);
public sealed record StartSearchBackupEntry(StartSearchValue? Before, StartSearchValue Written);
public sealed class StartSearchBackup
{
    public int Format { get; set; } = 1;
    public Dictionary<string, StartSearchBackupEntry> Entries { get; set; } = [];
}
public sealed class StartSearchService(IStartSearchBackend backend, string backupFile)
{
    private const string Policy = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    // Adapted from ChrisTitusTech/winutil at 2f0bdc8fca52e1ebe31a8d98216428061d3f8956 (MIT).
    public static readonly StartSearchKey Bing = new("ctt-bing", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", false);
    public static readonly StartSearchKey DeviceStart = new("ctt-start", @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection", true, true);
    public static readonly StartSearchKey Education = new("ctt-education", @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "IsEducationEnvironment", true, true);
    public static readonly StartSearchKey DeviceSection = new("ctt-section", Policy, "HideRecommendedSection", true, true);
    // Keep earlier IDs and user scopes so existing backups are never restored to a different address.
    public static readonly StartSearchKey LegacyBing = new("bing", Policy, "DisableSearchBoxSuggestions", false);
    public static readonly StartSearchKey Section = new("section", Policy, "HideRecommendedSection", true);
    public static readonly StartSearchKey Tips = new("tips", Advanced, "Start_IrisRecommendations", true);
    public static readonly StartSearchKey Documents = new("documents", Advanced, "Start_TrackDocs", true);
    public static readonly StartSearchKey NewApps = new("new-apps", Policy, "HideRecentlyAddedApps", true);
    public static readonly StartSearchKey Sites = new("sites", Policy, "HideRecommendedPersonalizedSites", true);
    public static IReadOnlyList<StartSearchKey> Keys { get; } = [Bing, DeviceStart, Education, DeviceSection, LegacyBing, Section, Tips, Documents, NewApps, Sites];
    private static readonly StartSearchKey[] LegacyKeys = [LegacyBing, Section, Tips, Documents, NewApps, Sites];
    private static readonly StartSearchKey[] WebDevicePolicies = [new("web", @"Software\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", false), new("disable-web", @"Software\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", false), new("mdm-web", @"Software\Microsoft\PolicyManager\current\device\Search", "DoNotUseWebResults_ProviderSet", false)];
    private bool BingManaged => WebDevicePolicies.Any(k => k.Id == "mdm-web" ? Is(backend.Read(k, true), 1) : backend.Read(k, true) is not null);
    private bool RecommendationsManaged => Is(backend.Read(new("mdm-start", DeviceStart.Path, "HideRecommendedSection_ProviderSet", true), true), 1)
        || Is(backend.Read(new("mdm-education", Education.Path, "IsEducationEnvironment_ProviderSet", true), true), 1);
    private static bool Is(StartSearchValue? value, int expected) => value == StartSearchValue.Dword(expected);
    private static Dictionary<string, StartSearchValue> RecommendationValues(bool disabled) => new()
    {
        [DeviceStart.Id] = StartSearchValue.Dword(disabled ? 1 : 0),
        [Education.Id] = StartSearchValue.Dword(disabled ? 1 : 0),
        [DeviceSection.Id] = StartSearchValue.Dword(disabled ? 1 : 0)
    };
    public StartSearchSnapshot Read()
    {
        var platform = backend.Platform();
        var client = platform.Windows.InstallationType.Equals("Client", StringComparison.OrdinalIgnoreCase);
        var bingManaged = BingManaged; var recManaged = RecommendationsManaged;
        var canBing = client && platform.Windows.Build >= 19041 && !bingManaged;
        var canRec = client && platform.Windows.Build >= 22000 && platform.IsAdministrator && !recManaged;
        var desired = RecommendationValues(true);
        var recCount = desired.Count(pair => backend.Read(Keys.Single(k => k.Id == pair.Key)) == pair.Value);
        var bingOff = Is(backend.Read(Bing), 0);
        var recOff = recCount == desired.Count;
        return new(platform.Windows.Display + $" · build {platform.Windows.Build}.{platform.Windows.Revision}", canBing, canRec, bingOff, recOff,
            bingManaged ? "Controlled by device policy" : bingOff ? "WinUtil setting: Bing search disabled" : "WinUtil setting: Bing search enabled/default",
            recManaged ? "Controlled by device management" : platform.Windows.Build < 22000 ? "Available on Windows 11" : recOff ? "WinUtil setting: Recommended section hidden" : recCount > 0 ? "WinUtil settings are partly configured" : "WinUtil setting: Recommended section shown/default",
            bingManaged ? "Change the device policy through its administrator or policy settings." : !client || platform.Windows.Build < 19041 ? "Requires Windows 10 version 2004 or later, or Windows 11 desktop." : "Chris Titus Tech WinUtil method. Local search stays available; other Windows policies can affect the result.",
            !client || platform.Windows.Build < 22000 ? "This control requires Windows 11 desktop." : !platform.IsAdministrator ? "Administrator access is required: WinUtil's recommendation tweak applies to all users." : "WinUtil method: hides the entire Recommended section, including recently added apps. Applies to all users and also disables lock-screen Windows Spotlight. Explorer will restart.",
            Load().Entries.Count > 0);
    }
    public bool Apply(bool? disableBing, bool? disableRecommendations, CancellationToken ct)
    {
        using var lease = Lock();
        var current = Read();
        if (disableBing.HasValue && !current.CanBing || disableRecommendations.HasValue && !current.CanRecommendations) throw new InvalidOperationException("The selected setting is unsupported or controlled by device policy. Check current settings again.");
        var desired = new Dictionary<string, StartSearchValue?>();
        if (disableBing.HasValue) desired[Bing.Id] = StartSearchValue.Dword(disableBing.Value ? 0 : 1);
        if (disableRecommendations.HasValue) foreach (var pair in RecommendationValues(disableRecommendations.Value)) desired[pair.Key] = pair.Value;
        if (desired.Count == 0) return false;
        var backup = Load();
        foreach (var (id, value) in desired)
        {
            var present = backend.Read(Keys.Single(k => k.Id == id));
            var before = backup.Entries.TryGetValue(id, out var owned) && present == owned.Written ? owned.Before : present;
            backup.Entries[id] = new(before, value!);
        }
        foreach (var legacy in LegacyKeys.Where(key => key.Recommendations ? disableRecommendations.HasValue : disableBing.HasValue))
        {
            if (backup.Entries.TryGetValue(legacy.Id, out var owned) && backend.Read(legacy) == owned.Written)
            {
                desired[legacy.Id] = owned.Before;
                backup.Entries.Remove(legacy.Id);
            }
        }
        Commit(desired, backup, ct);
        return disableRecommendations.HasValue;
    }
    public (int Restored, int Preserved, bool RestartExplorer) Restore(CancellationToken ct)
    {
        using var lease = Lock();
        var backup = Load(); var next = Load();
        var changes = new Dictionary<string, StartSearchValue?>();
        foreach (var (id, owned) in backup.Entries)
        {
            var key = Keys.Single(k => k.Id == id);
            if (key.Machine && RecommendationsManaged || key.Id == Bing.Id && BingManaged) continue;
            if (backend.Read(key) == owned.Written) { changes[id] = owned.Before; next.Entries.Remove(id); }
        }
        if (changes.Count > 0) Commit(changes, next, ct);
        return (changes.Count, backup.Entries.Count - changes.Count, changes.Keys.Any(id => Keys.Single(k => k.Id == id).Recommendations));
    }
    private void Commit(Dictionary<string, StartSearchValue?> desired, StartSearchBackup next, CancellationToken ct)
    {
        var previousBackup = Load();
        var before = desired.Keys.ToDictionary(id => id, id => backend.Read(Keys.Single(k => k.Id == id)));
        ct.ThrowIfCancellationRequested();
        // During restore, retain the original backup until restoration succeeds.
        var journal = new StartSearchBackup { Entries = new(previousBackup.Entries) };
        foreach (var (id, entry) in next.Entries) journal.Entries[id] = entry;
        Save(journal);
        var written = new List<string>();
        try
        {
            foreach (var (id, value) in desired)
            {
                ct.ThrowIfCancellationRequested();
                var key = Keys.Single(k => k.Id == id);
                written.Add(id); backend.Write(key, value);
                if (backend.Read(key) != value) throw new IOException("Windows did not confirm the saved setting: " + key.Name);
            }
            Save(next);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var id in written.AsEnumerable().Reverse())
                try { var key = Keys.Single(k => k.Id == id); if (backend.Read(key) == desired[id]) backend.Write(key, before[id]); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count == 0) Save(previousBackup);
            if (errors.Count > 0) throw new AggregateException("Some settings could not be rolled back. The restore backup was kept.", errors.Prepend(failure));
            throw;
        }
        backend.Notify();
    }
    private StartSearchBackup Load()
    {
        if (!File.Exists(backupFile)) return new();
        var state = JsonSerializer.Deserialize<StartSearchBackup>(File.ReadAllText(backupFile)) ?? throw new InvalidDataException("The Start & search backup is empty.");
        if (state.Format != 1 || state.Entries.Keys.Any(id => !Keys.Any(k => k.Id == id))) throw new InvalidDataException("The Start & search backup contains unsupported settings.");
        return state;
    }
    private void Save(StartSearchBackup state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        using (var stream = new FileStream(backupFile + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, state, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(backupFile + ".tmp", backupFile, true);
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        try { return new FileStream(backupFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another Start & search operation is running. Try again when it finishes.", e); }
    }
}
