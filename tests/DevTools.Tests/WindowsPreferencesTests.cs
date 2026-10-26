using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DevTools.Contracts;
using DevTools.Modules.Windows;
using Microsoft.Win32;

namespace DevTools.Tests;

internal static class WindowsPreferencesTests
{
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("All 17 selected WinUtil features belong to the five integrated Windows pages", () =>
        {
            Check(WindowsPreferenceDefinitions.All.Count == 17);
            Check(WindowsPreferenceDefinitions.All.Select(d => d.Id).Distinct().Count() == 17);
            Check(WindowsPreferencesModule.Pages.Select(p => p.Id).ToHashSet().SetEquals(WindowsPreferenceDefinitions.All.Select(d => d.Page)));
            Check(WindowsPreferenceDefinitions.All.SelectMany(d => d.Changes).Select(c => c.Resource.Id).Distinct().Count() == WindowsPreferenceDefinitions.All.Sum(d => d.Changes.Count));
        });
        run("Registry and service actions match the pinned WinUtil definitions, including inverted hibernation labels", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalogs", "windows-preferences-winutil.json")));
            var source = document.RootElement.GetProperty("tweaks");
            foreach (var definition in WindowsPreferenceDefinitions.All.Where(d => d.Id is not "classic-menu" and not "ultimate"))
            {
                var original = source.GetProperty(definition.SourceId);
                var registry = original.GetProperty("registry").EnumerateArray().ToArray();
                var changes = definition.Changes.Where(c => c.Resource.Kind == "registry").ToArray();
                Check(registry.Length == changes.Length);
                for (var i = 0; i < changes.Length; i++)
                {
                    var change = changes[i]; var expected = registry[i];
                    Check(expected.GetProperty("Path").GetString()!.Equals((change.Resource.Machine ? "HKLM:\\" : "HKCU:\\") + change.Resource.Path, StringComparison.OrdinalIgnoreCase));
                    Check(expected.GetProperty("Name").GetString() == change.Resource.Name);
                    var on = Value(expected, definition.Id == "hibernation" ? "OriginalValue" : "Value");
                    var off = Value(expected, definition.Id == "hibernation" ? "Value" : "OriginalValue");
                    Check(change.On == on && change.Off == off);
                }
                if (definition.Id == "location")
                {
                    var service = original.GetProperty("service")[0]; var action = definition.Changes.Single(c => c.Resource.Kind == "service");
                    Check(action.Resource.Path == service.GetProperty("Name").GetString() && action.On == StartSearchValue.Dword(4) && action.Off == StartSearchValue.Dword(3));
                }
            }
            Check(document.RootElement.GetProperty("ultimatePerformanceScript").GetString()!.Contains(WindowsPreferenceDefinitions.UltimateTemplate.ToString()));
            var classic = WindowsPreferenceDefinitions.All.Single(d => d.Id == "classic-menu").Changes.Single();
            Check(source.GetProperty("WPFTweaksRightClickMenu").GetProperty("InvokeScript")[0].GetString()!.Contains("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
            Check(classic.Resource.Kind == "classic" && classic.Resource.Path.EndsWith("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
        });
        run("Preference inspection does not write Windows values or create a backup", () =>
        {
            foreach (var page in WindowsPreferencesModule.Pages)
            {
                var f = new Fixture(artifacts, page.Id); var snapshot = f.Service.Read();
                Check(snapshot.Rows.Count > 0 && !snapshot.HasBackup && f.Backend.Writes == 0 && !File.Exists(f.Backup));
            }
        });
        run("Every selected feature applies its real resource set and restores original values", () =>
        {
            foreach (var definition in WindowsPreferenceDefinitions.All)
            {
                var f = new Fixture(artifacts, definition.Page); var before = new Dictionary<string, StartSearchValue>(f.Backend.Values);
                var initial = f.Service.Read().Rows.Single(r => r.Id == definition.Id).Enabled;
                var result = f.Service.ApplyAsync(new Dictionary<string, bool> { [definition.Id] = !initial }, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Changed > 0 && f.Service.Read().Rows.Single(r => r.Id == definition.Id).Enabled != initial && f.Backend.BackupAtFirstWrite);
                var restored = f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(restored.Preserved == 0 && !f.Service.Read().HasBackup && Same(before, f.Backend.Values));
            }
        });
        run("A page cannot apply or restore another page's resources", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar");
            Throws<InvalidDataException>(() => f.Service.ApplyAsync(new Dictionary<string, bool> { ["long-paths"] = true }, CancellationToken.None).GetAwaiter().GetResult());
            Directory.CreateDirectory(Path.GetDirectoryName(f.Backup)!);
            File.WriteAllText(f.Backup, JsonSerializer.Serialize(new PreferenceBackup { Entries = new() { ["long-paths"] = new(null, StartSearchValue.Dword(1)) } }));
            Throws<InvalidDataException>(() => f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult());
            Check(f.Backend.Writes == 0);
        });
        run("Preference restore preserves exact original value types and absence", () =>
        {
            var f = new Fixture(artifacts, "appearance-signin"); var original = new StartSearchValue(RegistryValueKind.String, "\"custom\""); f.Backend.Values["dark-apps"] = original;
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["dark-mode"] = true }, CancellationToken.None).GetAwaiter().GetResult();
            f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(f.Backend.Values["dark-apps"] == original && !f.Backend.Values.ContainsKey("dark-system"));
        });
        run("Repeated apply and Off preserve the first backup including removed policy values", () =>
        {
            var f = new Fixture(artifacts, "privacy-suggestions");
            foreach (var value in new[] { true, false, true }) f.Service.ApplyAsync(new Dictionary<string, bool> { ["consumer"] = value }, CancellationToken.None).GetAwaiter().GetResult();
            f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(!f.Backend.Values.ContainsKey("consumer") && !f.Service.Read().HasBackup);
        });
        run("External preference changes are preserved during restore", () =>
        {
            var f = new Fixture(artifacts, "appearance-signin");
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["dark-mode"] = true }, CancellationToken.None).GetAwaiter().GetResult();
            f.Backend.Values["dark-apps"] = StartSearchValue.Dword(7);
            var result = f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Changed == 1 && result.Preserved == 1 && f.Backend.Values["dark-apps"] == StartSearchValue.Dword(7));
        });
        run("Failed preference writes roll back earlier settings and the backup", () =>
        {
            var f = new Fixture(artifacts, "privacy-suggestions"); var before = new Dictionary<string, StartSearchValue>(f.Backend.Values); f.Backend.FailOnce = "location-sensor";
            Throws<IOException>(() => f.Service.ApplyAsync(new Dictionary<string, bool> { ["location"] = true }, CancellationToken.None).GetAwaiter().GetResult());
            Check(Same(before, f.Backend.Values) && !f.Service.Read().HasBackup);
        });
        run("Ignored preference writes fail verification and restore the backup", () =>
        {
            var f = new Fixture(artifacts, "developer-settings"); f.Backend.Ignore = true;
            Throws<IOException>(() => f.Service.ApplyAsync(new Dictionary<string, bool> { ["long-paths"] = true }, CancellationToken.None).GetAwaiter().GetResult());
            Check(!f.Service.Read().HasBackup);
        });
        run("Cancellation during preferences apply rolls back completed writes", () =>
        {
            var f = new Fixture(artifacts, "appearance-signin"); using var cancel = new CancellationTokenSource(); f.Backend.AfterWrite = () => cancel.Cancel();
            Throws<OperationCanceledException>(() => f.Service.ApplyAsync(new Dictionary<string, bool> { ["dark-mode"] = true }, cancel.Token).GetAwaiter().GetResult());
            Check(!f.Backend.Values.ContainsKey("dark-apps") && !f.Service.Read().HasBackup);
        });
        run("Preference controls enforce administrator, Windows version, edition and desktop requirements", () =>
        {
            var f = new Fixture(artifacts, "privacy-suggestions"); f.Backend.Edition = "Professional";
            Check(!f.Service.Read().Rows.Single(r => r.Id == "consumer").Available);
            f.Backend.Admin = false; Check(f.Service.Read().Rows.All(r => !r.Available));
            Throws<InvalidOperationException>(() => f.Service.ApplyAsync(new Dictionary<string, bool> { ["location"] = true }, CancellationToken.None).GetAwaiter().GetResult());
            var explorer = new Fixture(artifacts, "explorer-taskbar"); explorer.Backend.Build = 19045;
            Check(!explorer.Service.Read().Rows.Single(r => r.Id == "alignment").Available && explorer.Service.Read().Rows.Single(r => r.Id == "extensions").Available);
            var power = new Fixture(artifacts, "power-gaming"); power.Backend.Desktop = false;
            Check(!power.Service.Read().Rows.Single(r => r.Id == "ultimate").Available);
            Check(f.Backend.Writes == 0);
        });
        run("New device management blocks Apply and preserves owned values on Restore", () =>
        {
            var f = new Fixture(artifacts, "privacy-suggestions"); f.Service.ApplyAsync(new Dictionary<string, bool> { ["activity"] = true }, CancellationToken.None).GetAwaiter().GetResult();
            f.Backend.Managed = true;
            Check(!f.Service.Read().Rows.Single(r => r.Id == "activity").Available);
            var result = f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult(); Check(result.Preserved == 3 && result.Changed == 0);
        });
        run("Taskbar search box mode is shown as enabled and can be switched directly off", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar"); f.Backend.Values["search-button"] = StartSearchValue.Dword(2);
            Check(f.Service.Read().Rows.Single(r => r.Id == "search-button").Enabled);
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["search-button"] = false }, CancellationToken.None).GetAwaiter().GetResult();
            Check(f.Backend.Values["search-button"] == StartSearchValue.Dword(0));
            f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult(); Check(f.Backend.Values["search-button"] == StartSearchValue.Dword(2));
        });
        run("Ultimate Performance Off restores the previous custom plan without deleting unrelated schemes", () =>
        {
            var f = new Fixture(artifacts, "power-gaming"); var custom = Guid.NewGuid();
            f.Backend.Values["ultimate"] = WindowsPreferencesBackend.PowerValue(new(false, custom));
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["ultimate"] = true }, CancellationToken.None).GetAwaiter().GetResult();
            Check(WindowsPreferencesBackend.PowerState(f.Backend.Values["ultimate"]).Active == WindowsPreferenceDefinitions.ManagedPowerScheme);
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["ultimate"] = false }, CancellationToken.None).GetAwaiter().GetResult();
            Check(WindowsPreferencesBackend.PowerState(f.Backend.Values["ultimate"]) == new PreferencePowerState(false, custom));
        });
        run("Classic menu Off removes the handler and Restore preserves pre-existing empty keys", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar");
            f.Backend.Values["classic-menu"] = WindowsPreferencesBackend.ClassicValue(new(true, true, null));
            Check(f.Service.Read().Rows.Single(r => r.Id == "classic-menu").Enabled);
            f.Service.ApplyAsync(new Dictionary<string, bool> { ["classic-menu"] = false }, CancellationToken.None).GetAwaiter().GetResult();
            Check(WindowsPreferencesBackend.ClassicState(f.Backend.Values["classic-menu"]) == new PreferenceClassicState(true, false, null));
            f.Service.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(WindowsPreferencesBackend.ClassicState(f.Backend.Values["classic-menu"]) == new PreferenceClassicState(true, true, null));
        });
        run("Declined preference Apply changes nothing and successful Explorer Apply/Restore each restart once", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar"); var context = new ScoopTests.TestContext(Path.Combine(artifacts, Guid.NewGuid().ToString("N")));
            var w = new WindowsPreferencesModule("explorer-taskbar", f.Backend).CreatePage(context).PreferencesWorkspace!;
            Act(w.CheckAction!, context); var row = w.Rows.Single(r => r.Id == "extensions"); row.Enabled = true;
            context.Confirm = false; Act(w.ApplyAction!, context); Check(f.Backend.Writes == 0 && f.Backend.Restarts == 0 && row.IsChanged);
            context.Confirm = true; Act(w.ApplyAction!, context); Check(f.Backend.Restarts == 1 && w.HasBackup && !w.HasChanges && context.Preview.Contains("windows will close"));
            Act(w.RestoreAction!, context); Check(f.Backend.Restarts == 2 && !w.HasBackup && !row.Enabled);
        });
        run("Explorer refresh is completed after saved settings even if cancellation arrives at the final write", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar"); var context = new ScoopTests.TestContext(Path.Combine(artifacts, Guid.NewGuid().ToString("N")));
            var w = new WindowsPreferencesModule("explorer-taskbar", f.Backend).CreatePage(context).PreferencesWorkspace!;
            Act(w.CheckAction!, context); w.Rows.Single(r => r.Id == "extensions").Enabled = true;
            f.Backend.AfterWrite = context.Cancel; Act(w.ApplyAction!, context);
            Check(f.Backend.Restarts == 1 && w.HasBackup && w.Result.Contains("File Explorer restarted"));
        });
        run("Explorer restart failure retains preference backup and exposes a retry", () =>
        {
            var f = new Fixture(artifacts, "explorer-taskbar"); f.Backend.FailRestart = true; var context = new ScoopTests.TestContext(Path.Combine(artifacts, Guid.NewGuid().ToString("N")));
            var w = new WindowsPreferencesModule("explorer-taskbar", f.Backend).CreatePage(context).PreferencesWorkspace!;
            Act(w.CheckAction!, context); w.Rows.Single(r => r.Id == "extensions").Enabled = true; Act(w.ApplyAction!, context);
            Check(w.HasBackup && w.Result.Contains("still needs a restart"));
            f.Backend.FailRestart = false; Act(w.RestartAction!, context); Check(f.Backend.Restarts == 2);
        });
    }
    internal static ModulePage Preview(string artifacts, string id)
    {
        var backend = new FakeBackend(); var context = new ScoopTests.TestContext(Path.Combine(artifacts, Guid.NewGuid().ToString("N")));
        var page = new WindowsPreferencesModule(id, backend).CreatePage(context); Act(page.PreferencesWorkspace!.CheckAction!, context); return page;
    }
    private static StartSearchValue? Value(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetString();
        if (value == "<RemoveEntry>") return null;
        return element.GetProperty("Type").GetString() == "DWord" ? StartSearchValue.Dword(int.Parse(value!)) : new(RegistryValueKind.String, JsonSerializer.Serialize(value));
    }
    private static bool Same(Dictionary<string, StartSearchValue> a, Dictionary<string, StartSearchValue> b) => a.Count == b.Count && a.All(pair => b.GetValueOrDefault(pair.Key) == pair.Value);
    private static void Act(ModuleAction action, ScoopTests.TestContext context)
    {
        action.Execute(null); var deadline = DateTime.UtcNow.AddSeconds(10);
        while (context.IsOperationRunning && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
        Check(!context.IsOperationRunning);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Windows preferences assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Fixture
    {
        internal readonly FakeBackend Backend = new();
        internal readonly string Backup;
        internal readonly WindowsPreferencesService Service;
        internal Fixture(string artifacts, string page)
        {
            Backup = Path.Combine(artifacts, "preferences-" + Guid.NewGuid().ToString("N"), page + ".json"); Backend.Backup = Backup; Service = new(Backend, page, Backup);
        }
    }
    private sealed class FakeBackend : IWindowsPreferencesBackend
    {
        internal readonly Dictionary<string, StartSearchValue> Values = new()
        {
            ["location-service"] = StartSearchValue.Dword(3),
            ["classic-menu"] = WindowsPreferencesBackend.ClassicValue(new(false, false, null)),
            ["hibernation"] = StartSearchValue.Dword(1),
            ["hibernate-reg"] = StartSearchValue.Dword(1),
            ["hibernate-menu"] = StartSearchValue.Dword(1),
            ["ultimate"] = WindowsPreferencesBackend.PowerValue(new(false, WindowsPreferenceDefinitions.BalancedScheme))
        };
        internal string Edition = "Enterprise", Backup = "";
        internal int Build = 22621, Writes, Restarts;
        internal bool Admin = true, Desktop = true, Managed, Ignore, BackupAtFirstWrite, FailRestart;
        internal string? FailOnce;
        internal Action? AfterWrite;
        public PreferencesPlatform Platform() => new(new(new(Edition, Build, "Windows", "Client", 2506), Admin), Desktop);
        public StartSearchValue? Read(PreferenceResource resource) => resource.Id == "management-check" ? Managed ? StartSearchValue.Dword(1) : null : Values.GetValueOrDefault(resource.Id);
        public Task WriteAsync(PreferenceResource resource, StartSearchValue? value)
        {
            Writes++; if (Writes == 1) BackupAtFirstWrite = File.Exists(Backup);
            if (FailOnce == resource.Id) { FailOnce = null; throw new IOException("Simulated Windows write failure."); }
            if (!Ignore) { if (value is null) Values.Remove(resource.Id); else Values[resource.Id] = value; }
            AfterWrite?.Invoke(); return Task.CompletedTask;
        }
        public void Notify() { }
        public Task RestartExplorerAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Restarts++; if (FailRestart) throw new IOException("Simulated Explorer restart failure."); return Task.CompletedTask; }
    }
}
