using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DevTools.Contracts;
using DevTools.Modules.Windows;
using Microsoft.Win32;

namespace DevTools.Tests;

internal static class StartSearchTests
{
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Native settings match the pinned WinUtil registry definitions", () =>
        {
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalogs", "start-search-winutil.json")));
            var tweaks = source.RootElement.GetProperty("tweaks");
            foreach (var (name, keys) in new[] { ("WPFToggleBingSearch", new[] { StartSearchService.Bing }), ("WPFToggleStartMenuRecommendations", new[] { StartSearchService.DeviceStart, StartSearchService.Education, StartSearchService.DeviceSection }) })
            {
                var definitions = tweaks.GetProperty(name).GetProperty("registry").EnumerateArray().ToArray(); Check(definitions.Length == keys.Length);
                for (var i = 0; i < keys.Length; i++)
                {
                    var definition = definitions[i]; var key = keys[i];
                    Check(definition.GetProperty("Path").GetString()!.Equals((key.Machine ? "HKLM:\\" : "HKCU:\\") + key.Path, StringComparison.OrdinalIgnoreCase));
                    Check(definition.GetProperty("Name").GetString() == key.Name && definition.GetProperty("Type").GetString() == "DWord");
                    Check(definition.GetProperty("OriginalValue").GetString() == (key.Recommendations ? "1" : "0"));
                }
            }
        });
        run("Legacy backups restore only their original user addresses", () =>
        {
            var f = new Fixture(artifacts); f.Backend.User[StartSearchService.LegacyBing.Id] = StartSearchValue.Dword(1);
            Directory.CreateDirectory(Path.GetDirectoryName(f.Backup)!);
            File.WriteAllText(f.Backup, JsonSerializer.Serialize(new StartSearchBackup { Entries = new() { [StartSearchService.LegacyBing.Id] = new(null, StartSearchValue.Dword(1)) } }));
            var result = f.Service.Restore(CancellationToken.None);
            Check(result.Restored == 1 && f.Backend.User.Count == 0 && f.Backend.Machine.Count == 0);
        });
        run("Switching to WinUtil restores owned old content tweaks in the same transaction", () =>
        {
            var f = new Fixture(artifacts); f.Backend.User[StartSearchService.Documents.Id] = StartSearchValue.Dword(0);
            Directory.CreateDirectory(Path.GetDirectoryName(f.Backup)!);
            File.WriteAllText(f.Backup, JsonSerializer.Serialize(new StartSearchBackup { Entries = new() { [StartSearchService.Documents.Id] = new(StartSearchValue.Dword(1), StartSearchValue.Dword(0)) } }));
            f.Service.Apply(null, true, CancellationToken.None);
            Check(f.Backend.Read(StartSearchService.Documents) == StartSearchValue.Dword(1) && f.Backend.Machine.Count == 3);
            var saved = JsonSerializer.Deserialize<StartSearchBackup>(File.ReadAllText(f.Backup))!;
            Check(saved.Entries.Count == 3 && !saved.Entries.ContainsKey(StartSearchService.Documents.Id));
        });
        run("Restore preserves machine settings when device management takes ownership", () =>
        {
            var f = new Fixture(artifacts); f.Service.Apply(null, true, CancellationToken.None);
            f.Backend.Machine["mdm-start"] = StartSearchValue.Dword(1);
            var result = f.Service.Restore(CancellationToken.None);
            Check(result.Restored == 0 && result.Preserved == 3 && !result.RestartExplorer && f.Service.Read().HasBackup);
        });
        run("Declining the Explorer restart confirmation applies neither selected row", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!) { Confirm = false };
            var w = new StartSearchModule(f.Backend).CreatePage(context).StartSearchWorkspace!;
            Act(w.CheckAction!, context); w.DisableBing = true; w.DisableRecommendations = true;
            Act(w.ApplyAction!, context);
            Check(f.Backend.Writes == 0 && f.Backend.Restarts == 0 && context.Preview.Contains("Spotlight") && context.Preview.Contains("all users"));
        });
        run("Recommendation apply and restore restart Explorer after confirmed successful writes", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!);
            var w = new StartSearchModule(f.Backend).CreatePage(context).StartSearchWorkspace!;
            Act(w.CheckAction!, context); w.DisableRecommendations = true; Act(w.ApplyAction!, context);
            Check(f.Backend.Restarts == 1 && w.DisableRecommendations && w.HasBackup && context.Preview.Contains("Spotlight"));
            Act(w.RestoreAction!, context);
            Check(f.Backend.Restarts == 2 && !w.DisableRecommendations && !w.HasBackup && f.Backend.Machine.Count == 0);
        });
        run("Explorer restart failure keeps saved settings and permits a manual retry", () =>
        {
            var f = new Fixture(artifacts); f.Backend.FailRestart = true; var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!);
            var w = new StartSearchModule(f.Backend).CreatePage(context).StartSearchWorkspace!;
            Act(w.CheckAction!, context); w.DisableRecommendations = true; Act(w.ApplyAction!, context);
            Check(w.HasBackup && w.DisableRecommendations && w.Result.Contains("still needs a restart") && f.Backend.Machine.Count == 3);
            f.Backend.FailRestart = false; Act(w.RestartAction!, context);
            Check(f.Backend.Restarts == 2 && w.Result.Contains("Explorer restarted"));
        });
        run("Saved Start recommendation changes always restart Explorer despite late cancellation", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!);
            var w = new StartSearchModule(f.Backend).CreatePage(context).StartSearchWorkspace!;
            Act(w.CheckAction!, context); w.DisableRecommendations = true;
            f.Backend.AfterWrite = () => { if (f.Backend.Writes == 3) context.Cancel(); };
            Act(w.ApplyAction!, context);
            Check(w.DisableRecommendations && w.HasBackup && f.Backend.Restarts == 1 && w.Result.Contains("Explorer restarted"));
        });
        run("Start/search inspection reads current flags without creating a backup or changing Windows", () =>
        {
            var f = new Fixture(artifacts); var status = f.Service.Read();
            Check(status.CanBing && status.CanRecommendations && !status.BingDisabled && !status.RecommendationsDisabled && !status.HasBackup && f.Backend.Writes == 0 && !File.Exists(f.Backup));
        });
        run("Bing search applies only its user flag and backs up the original value before writing", () =>
        {
            var f = new Fixture(artifacts); var original = new StartSearchValue(RegistryValueKind.String, JsonSerializer.Serialize("custom"));
            f.Backend.User[StartSearchService.Bing.Id] = original;
            f.Service.Apply(true, null, CancellationToken.None);
            Check(f.Backend.Read(StartSearchService.Bing) == StartSearchValue.Dword(0) && f.Backend.Writes == 1 && f.Backend.BackupPresentAtFirstWrite);
            Check(!f.Backend.User.ContainsKey(StartSearchService.Documents.Id));
            f.Service.Restore(CancellationToken.None); Check(f.Backend.Read(StartSearchService.Bing) == original);
        });
        run("WinUtil hides the whole Recommended section using exactly its three device-wide flags", () =>
        {
            var f = new Fixture(artifacts); var restart = f.Service.Apply(null, true, CancellationToken.None);
            Check(restart && f.Backend.Machine.Count == 3 && f.Backend.User.Count == 0);
            foreach (var key in new[] { StartSearchService.DeviceStart, StartSearchService.Education, StartSearchService.DeviceSection })
                Check(key.Machine && f.Backend.Read(key) == StartSearchValue.Dword(1));
            Check(f.Service.Read().RecommendationsDisabled && f.Service.Read().RecommendationsHint.Contains("Spotlight"));
        });
        run("WinUtil recommendation changes require administrator access but Bing remains a user setting", () =>
        {
            var f = new Fixture(artifacts); f.Backend.Administrator = false;
            Check(f.Service.Read().CanBing && !f.Service.Read().CanRecommendations);
            try { f.Service.Apply(null, true, CancellationToken.None); throw new Exception("Non-admin device change accepted."); } catch (InvalidOperationException) { }
            Check(f.Backend.Writes == 0);
        });
        run("Repeated apply keeps the first original backup and restore removes values that were originally absent", () =>
        {
            var f = new Fixture(artifacts); f.Backend.User[StartSearchService.Documents.Id] = StartSearchValue.Dword(1);
            f.Service.Apply(true, true, CancellationToken.None); f.Service.Apply(true, true, CancellationToken.None);
            f.Service.Apply(false, false, CancellationToken.None); var result = f.Service.Restore(CancellationToken.None);
            Check(result.Restored == 4 && result.Preserved == 0 && f.Backend.User.Count == 1 && f.Backend.Read(StartSearchService.Documents) == StartSearchValue.Dword(1) && f.Backend.Machine.Count == 0 && !f.Service.Read().HasBackup);
        });
        run("Restore preserves settings changed by another app after DevTools applied them", () =>
        {
            var f = new Fixture(artifacts); f.Service.Apply(true, true, CancellationToken.None);
            f.Backend.User[StartSearchService.Bing.Id] = StartSearchValue.Dword(1);
            var result = f.Service.Restore(CancellationToken.None);
            Check(result.Restored == 3 && result.Preserved == 1 && f.Backend.Read(StartSearchService.Bing) == StartSearchValue.Dword(1));
        });
        run("Machine-managed Start/search settings are displayed and cannot be overwritten by Apply", () =>
        {
            var f = new Fixture(artifacts); f.Backend.Machine["web"] = StartSearchValue.Dword(0); f.Backend.Machine["mdm-start"] = StartSearchValue.Dword(1);
            var state = f.Service.Read(); Check(!state.CanBing && !state.CanRecommendations && state.BingState.Contains("device policy"));
            try { f.Service.Apply(true, null, CancellationToken.None); throw new Exception("Managed setting was accepted."); } catch (InvalidOperationException) { }
            Check(f.Backend.Writes == 0);
        });
        run("Unsupported Windows editions/builds leave controls unavailable without writing unsupported flags", () =>
        {
            var f = new Fixture(artifacts); f.Backend.Windows = new("Professional", 19045, "Windows 10");
            Check(f.Service.Read().CanBing && !f.Service.Read().CanRecommendations);
            f.Backend.Windows = new("ServerStandard", 26100, "Windows Server", "Server");
            Check(!f.Service.Read().CanBing && !f.Service.Read().CanRecommendations);
            Check(f.Backend.Writes == 0);
        });
        run("An existing WinUtil machine value is not mistaken for a management restriction", () =>
        {
            var f = new Fixture(artifacts); f.Backend.Machine[StartSearchService.DeviceSection.Id] = StartSearchValue.Dword(1);
            Check(f.Service.Read().CanRecommendations && !f.Service.Read().RecommendationsDisabled);
            f.Service.Apply(null, true, CancellationToken.None);
            Check(f.Service.Read().RecommendationsDisabled);
            f.Service.Restore(CancellationToken.None);
            Check(f.Backend.Machine.Count == 1 && f.Backend.Read(StartSearchService.DeviceSection) == StartSearchValue.Dword(1));
        });
        run("A failed WinUtil device write rolls back earlier user/device writes and original backups", () =>
        {
            var f = new Fixture(artifacts); f.Backend.User[StartSearchService.Bing.Id] = StartSearchValue.Dword(1);
            f.Backend.FailOnce = StartSearchService.Education.Id;
            try { f.Service.Apply(true, true, CancellationToken.None); throw new Exception("Expected registry write failure."); } catch (IOException) { }
            Check(f.Backend.User.Count == 1 && f.Backend.Machine.Count == 0 && f.Backend.Read(StartSearchService.Bing) == StartSearchValue.Dword(1) && !f.Service.Read().HasBackup);
        });
        run("Read-back verification detects ignored settings and does not report success", () =>
        {
            var f = new Fixture(artifacts); f.Backend.IgnoreWrites = true;
            try { f.Service.Apply(true, null, CancellationToken.None); throw new Exception("Ignored write was accepted."); } catch (IOException) { }
            Check(f.Backend.User.Count == 0);
        });
        run("Cancellation before Apply changes neither Windows flags nor the restore backup", () =>
        {
            var f = new Fixture(artifacts); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { f.Service.Apply(true, true, cancel.Token); throw new Exception("Cancelled apply accepted."); } catch (OperationCanceledException) { }
            Check(f.Backend.Writes == 0 && !File.Exists(f.Backup));
        });
        run("Restore rejects foreign backup addresses before any registry writes", () =>
        {
            var f = new Fixture(artifacts); Directory.CreateDirectory(Path.GetDirectoryName(f.Backup)!);
            File.WriteAllText(f.Backup, JsonSerializer.Serialize(new StartSearchBackup { Entries = new() { ["unrelated"] = new(null, StartSearchValue.Dword(1)) } }));
            try { f.Service.Restore(CancellationToken.None); throw new Exception("Foreign backup accepted."); } catch (InvalidDataException) { }
            Check(f.Backend.Writes == 0);
        });
        run("Compact Start/search controls apply only changed rows and refresh their saved state", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!); var page = new StartSearchModule(f.Backend).CreatePage(context); var w = page.StartSearchWorkspace!;
            Act(w.CheckAction!, context); Check(w.IsInitialized && !w.ApplyAction!.CanExecute(null) && f.Backend.Writes == 0);
            w.DisableBing = true; Check(w.HasChanges && w.ApplyAction!.CanExecute(null));
            Act(w.ApplyAction!, context); Check(!w.HasChanges && w.HasBackup && w.Result.Contains("Sign out") && f.Backend.Writes == 1 && !page.Status.StartsWith("Failed"));
            Act(w.RestoreAction!, context); Check(!w.DisableBing && !w.HasBackup && f.Backend.User.Count == 0);
        });
    }
    internal static ModulePage Preview(string artifacts, bool administrator = true, bool managed = false)
    {
        var f = new Fixture(artifacts); f.Backend.Administrator = administrator;
        if (managed) { f.Backend.Machine["web"] = StartSearchValue.Dword(0); f.Backend.Machine["mdm-start"] = StartSearchValue.Dword(1); }
        var context = new ScoopTests.TestContext(Path.GetDirectoryName(f.Backup)!); var page = new StartSearchModule(f.Backend).CreatePage(context);
        Act(page.StartSearchWorkspace!.CheckAction!, context); return page;
    }
    private static void Act(ModuleAction action, ScoopTests.TestContext context)
    {
        action.Execute(null); var deadline = DateTime.UtcNow.AddSeconds(10);
        while (context.IsOperationRunning && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
        Check(!context.IsOperationRunning);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Start/search assertion failed."); }
    private sealed class Fixture
    {
        internal readonly string Backup;
        internal readonly FakeBackend Backend = new();
        internal readonly StartSearchService Service;
        internal Fixture(string artifacts) { Backup = Path.Combine(artifacts, "start-search-" + Guid.NewGuid().ToString("N"), "StartSearch", "backup.json"); Backend.Backup = Backup; Service = new(Backend, Backup); }
    }
    private sealed class FakeBackend : IStartSearchBackend
    {
        internal readonly Dictionary<string, StartSearchValue> User = [], Machine = [];
        internal WindowsEdition Windows = new("Professional", 22621, "Windows", "Client", 2506);
        internal bool Administrator = true;
        internal bool IgnoreWrites, BackupPresentAtFirstWrite, FailRestart;
        internal string Backup = "";
        internal string? FailOnce;
        internal int Writes, Restarts;
        internal Action? AfterWrite;
        public StartSearchPlatform Platform() => new(Windows, Administrator);
        public StartSearchValue? Read(StartSearchKey key, bool machine = false) => (machine || key.Machine ? Machine : User).GetValueOrDefault(key.Id);
        public void Write(StartSearchKey key, StartSearchValue? value)
        {
            Writes++;
            if (Writes == 1) BackupPresentAtFirstWrite = File.Exists(Backup);
            if (FailOnce == key.Id) { FailOnce = null; throw new IOException("Simulated write failure."); }
            if (IgnoreWrites) return;
            var values = key.Machine ? Machine : User;
            if (value is null) values.Remove(key.Id); else values[key.Id] = value;
            AfterWrite?.Invoke();
        }
        public void Notify() { }
        public Task RestartExplorerAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Restarts++; if (FailRestart) throw new IOException("Simulated Explorer failure."); return Task.CompletedTask; }
    }
}
