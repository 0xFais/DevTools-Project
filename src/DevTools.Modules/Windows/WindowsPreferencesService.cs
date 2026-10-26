using System.Text.Json;
using Microsoft.Win32;

namespace DevTools.Modules.Windows;

public sealed record PreferenceSnapshot(string Id, bool Enabled, bool Available, string State, string Availability);
public sealed record PreferencesSnapshot(string Platform, IReadOnlyList<PreferenceSnapshot> Rows, bool HasBackup);
public sealed record PreferencesApplyResult(int Changed, int Preserved, bool RestartExplorer, bool RestartWindows);
public sealed record PreferenceBackupEntry(StartSearchValue? Before, StartSearchValue? Written);
public sealed class PreferenceBackup
{
    public int Format { get; set; } = 1;
    public Dictionary<string, PreferenceBackupEntry> Entries { get; set; } = [];
}

public sealed class WindowsPreferencesService(IWindowsPreferencesBackend backend, string pageId, string backupFile)
{
    private IReadOnlyList<PreferenceDefinition> Definitions => WindowsPreferenceDefinitions.All.Where(d => d.Page == pageId).ToArray();
    private PreferenceResource Resource(string id) => Definitions.SelectMany(d => d.Changes).Single(c => c.Resource.Id == id).Resource;
    private string? Restriction(PreferenceDefinition definition, PreferencesPlatform platform)
    {
        var system = platform.System;
        if (!system.Windows.InstallationType.Equals("Client", StringComparison.OrdinalIgnoreCase) || system.Windows.Build < definition.MinBuild) return definition.MinBuild >= 22000 ? "Requires Windows 11 desktop." : "Requires Windows 10 version 2004 or later, or Windows 11 desktop.";
        if (definition.RequiresAdmin && !system.IsAdministrator) return "Administrator access is required.";
        if (definition.EnterpriseOnly && !(system.Windows.Id.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || system.Windows.Id.Contains("Education", StringComparison.OrdinalIgnoreCase))) return "This Microsoft policy requires Enterprise or Education; it is not supported on Home/Pro.";
        if (definition.Id == "ultimate" && !platform.Desktop) return "Available on desktops without a detected battery; hardware may restrict available power plans.";
        foreach (var (path, name) in ManagedProviders(definition.Id))
            if (backend.Read(new("management-check", "registry", true, @"SOFTWARE\Microsoft\PolicyManager\current\device\" + path, name + "_ProviderSet")) == StartSearchValue.Dword(1)) return "Controlled by device management.";
        return null;
    }
    private static IEnumerable<(string Path, string Name)> ManagedProviders(string id) => id switch
    {
        "activity" => [("System", "AllowActivityFeed")],
        "location" => [("System", "AllowLocation"), ("Privacy", "LetAppsAccessLocation")],
        "consumer" => [("Experience", "AllowWindowsConsumerFeatures")],
        _ => []
    };
    public PreferencesSnapshot Read()
    {
        var platform = backend.Platform();
        var rows = Definitions.Select(definition =>
        {
            try
            {
                var restriction = Restriction(definition, platform);
                var values = definition.Changes.Select(c => (Change: c, Value: backend.Read(c.Resource))).ToArray();
                if (values.Any(v => v.Change.Resource.Kind == "service" && v.Value is null)) restriction ??= "The required Windows service is not installed.";
                var enabled = values.All(v => Match(definition, v.Change, v.Value, true));
                var disabled = values.All(v => Match(definition, v.Change, v.Value, false));
                return new PreferenceSnapshot(definition.Id, enabled, restriction is null,
                    restriction ?? (enabled ? "Saved setting: On" : disabled ? "Saved setting: Off/default" : "Saved setting: custom or partly configured"),
                    restriction ?? definition.Scope);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            { return new PreferenceSnapshot(definition.Id, false, false, "Could not inspect this setting", e.Message); }
        }).ToArray();
        return new(platform.System.Windows.Display + $" · build {platform.System.Windows.Build}.{platform.System.Windows.Revision}", rows, Load().Entries.Count > 0);
    }
    private static bool Match(PreferenceDefinition definition, PreferenceChange change, StartSearchValue? value, bool enabled)
    {
        if (change.Resource.Kind == "power")
        {
            var state = WindowsPreferencesBackend.PowerState(value!);
            return enabled == (state.Exists && state.Active == WindowsPreferenceDefinitions.ManagedPowerScheme);
        }
        if (change.Resource.Kind == "classic") return enabled == WindowsPreferencesBackend.ClassicState(value!).HandlerExists;
        if (definition.Id == "search-button" && value?.Kind == RegistryValueKind.DWord) return enabled == (JsonSerializer.Deserialize<int>(value.Json) > 0);
        if (value is null) return definition.DefaultOn == enabled;
        return value == (enabled ? change.On : change.Off);
    }
    public async Task<PreferencesApplyResult> ApplyAsync(IReadOnlyDictionary<string, bool> selected, CancellationToken ct)
    {
        using var lease = Lock();
        ct.ThrowIfCancellationRequested();
        if (selected.Keys.Any(id => !Definitions.Any(d => d.Id == id))) throw new InvalidDataException("A selected setting is outside this Windows page.");
        var states = Read();
        if (selected.Any(pair => !states.Rows.Single(r => r.Id == pair.Key).Available)) throw new InvalidOperationException("A selected setting is unavailable. Check current settings again.");
        var backup = Load(); var changes = new Dictionary<string, StartSearchValue?>();
        foreach (var definition in Definitions.Where(d => selected.ContainsKey(d.Id)))
            foreach (var change in definition.Changes)
            {
                var resource = change.Resource;
                var present = backend.Read(resource);
                var value = selected[definition.Id] ? change.On : change.Off;
                if (resource.Kind == "classic")
                {
                    var current = WindowsPreferencesBackend.ClassicState(present!);
                    var parentExisted = backup.Entries.TryGetValue(resource.Id, out var original) && original.Before is not null ? WindowsPreferencesBackend.ClassicState(original.Before).ClassExists : current.ClassExists;
                    value = WindowsPreferencesBackend.ClassicValue(selected[definition.Id] ? new(true, true, new(RegistryValueKind.String, "\"\"")) : new(parentExisted, false, null));
                }
                if (resource.Kind == "power")
                {
                    var current = WindowsPreferencesBackend.PowerState(present!);
                    var active = current.Active;
                    if (!selected[definition.Id] && active == WindowsPreferenceDefinitions.ManagedPowerScheme)
                    {
                        active = backup.Entries.TryGetValue(resource.Id, out var original) && original.Before is not null ? WindowsPreferencesBackend.PowerState(original.Before).Active : WindowsPreferenceDefinitions.BalancedScheme;
                        if (active == WindowsPreferenceDefinitions.ManagedPowerScheme) active = WindowsPreferenceDefinitions.BalancedScheme;
                    }
                    value = WindowsPreferencesBackend.PowerValue(new(selected[definition.Id], selected[definition.Id] ? WindowsPreferenceDefinitions.ManagedPowerScheme : active));
                }
                if (present == value) continue;
                var before = backup.Entries.TryGetValue(resource.Id, out var owned) && present == owned.Written ? owned.Before : present;
                changes[resource.Id] = value;
                // Absence is a meaningful written state, including removal of policy values.
                backup.Entries[resource.Id] = new(before, value!);
            }
        if (changes.Count > 0) await CommitAsync(changes, backup, ct);
        return Result(changes.Keys, 0);
    }
    public async Task<PreferencesApplyResult> RestoreAsync(CancellationToken ct)
    {
        using var lease = Lock();
        ct.ThrowIfCancellationRequested();
        var backup = Load(); var next = Load(); var changes = new Dictionary<string, StartSearchValue?>();
        var platform = backend.Platform();
        foreach (var (id, owned) in backup.Entries)
        {
            var definition = Definitions.Single(d => d.Changes.Any(c => c.Resource.Id == id));
            if (Restriction(definition, platform) is not null) continue;
            if (backend.Read(Resource(id)) == owned.Written) { changes[id] = owned.Before; next.Entries.Remove(id); }
        }
        if (changes.Count > 0) await CommitAsync(changes, next, ct);
        return Result(changes.Keys, backup.Entries.Count - changes.Count);
    }
    private PreferencesApplyResult Result(IEnumerable<string> keys, int preserved)
    {
        var changed = keys.ToHashSet();
        var definitions = Definitions.Where(d => d.Changes.Any(c => changed.Contains(c.Resource.Id))).ToArray();
        return new(changed.Count, preserved, definitions.Any(d => d.RestartExplorer), definitions.Any(d => d.RestartWindows));
    }
    private async Task CommitAsync(Dictionary<string, StartSearchValue?> changes, PreferenceBackup next, CancellationToken ct)
    {
        var previous = Load();
        var before = changes.Keys.ToDictionary(id => id, id => backend.Read(Resource(id)));
        ct.ThrowIfCancellationRequested();
        var journal = new PreferenceBackup { Entries = new(previous.Entries) };
        foreach (var pair in next.Entries) journal.Entries[pair.Key] = pair.Value;
        Save(journal);
        var written = new List<string>();
        try
        {
            foreach (var (id, value) in changes.OrderBy(pair => Resource(pair.Key).Kind == "registry" ? 1 : 0))
            {
                ct.ThrowIfCancellationRequested();
                written.Add(id);
                await backend.WriteAsync(Resource(id), value);
                if (backend.Read(Resource(id)) != value) throw new IOException("Windows did not confirm the saved setting: " + id);
            }
            Save(next);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            // Power/hibernation operations precede dependent registry writes, also during rollback.
            foreach (var id in written.OrderBy(id => Resource(id).Kind == "registry" ? 1 : 0))
                try
                {
                    var current = backend.Read(Resource(id));
                    if (current == changes[id]) { await backend.WriteAsync(Resource(id), before[id]); if (backend.Read(Resource(id)) != before[id]) throw new IOException("Rollback could not be verified: " + id); }
                    else if (current != before[id]) throw new IOException("An incomplete change needs recovery: " + id);
                }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count == 0) Save(previous);
            else throw new AggregateException("Some changes could not be rolled back. The restore backup was kept.", errors.Prepend(failure));
            throw;
        }
        backend.Notify();
    }
    private PreferenceBackup Load()
    {
        if (!File.Exists(backupFile)) return new();
        var state = JsonSerializer.Deserialize<PreferenceBackup>(File.ReadAllText(backupFile)) ?? throw new InvalidDataException("The Windows preference backup is empty.");
        if (state.Format != 1 || state.Entries is null || state.Entries.Keys.Any(id => !Definitions.Any(d => d.Changes.Any(c => c.Resource.Id == id)))) throw new InvalidDataException("The Windows preference backup contains unsupported settings.");
        return state;
    }
    private void Save(PreferenceBackup state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        using (var stream = new FileStream(backupFile + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, state, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(backupFile + ".tmp", backupFile, true);
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        try { return new FileStream(backupFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another operation is changing this Windows page. Try again when it finishes.", e); }
    }
}
