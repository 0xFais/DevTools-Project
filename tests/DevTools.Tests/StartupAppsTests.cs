using System.IO;
using DevTools.Contracts;
using DevTools.Modules.Windows;

namespace DevTools.Tests;

internal static class StartupAppsTests
{
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Refresh retains same-name entries from separate sources and exposes partial-read errors", () =>
        {
            var backend = new FakeBackend
            {
                Snapshot = new StartupReadResult([
                    new("Tool", "HKCU Run (64-bit)", "Current user", "\"C:\\Tool.exe\"", "Unknown (no StartupApproved value)", RegistryIdentity(StartupHive.CurrentUser, StartupView.Registry64, "Tool", StartupRegistryKind.String, "\"C:\\Tool.exe\"")),
                    new("Tool", "HKLM Run (32-bit)", "All users", "\"C:\\Program Files (x86)\\Tool.exe\"", "Disabled (StartupApproved)", RegistryIdentity(StartupHive.LocalMachine, StartupView.Registry32, "Tool", StartupRegistryKind.String, "\"C:\\Program Files (x86)\\Tool.exe\"")),
                    new("Tool", "User Startup folder", "Current user", "\"C:\\Startup\\Tool.lnk\"", "Unknown (no StartupApproved value)", new(StartupEntryType.StartupFile, Path: @"C:\Startup\Tool.lnk", StartupRoot: @"C:\Startup", Sha256: "ab"))
                ], ["HKLM Run (64-bit): access denied"])
            };
            var context = new TestContext(Path.Combine(artifacts, "startup-refresh-data"));
            var workspace = new StartupAppsModule(backend).CreatePage(context).StartupAppsWorkspace!;
            workspace.RefreshAction!.Execute(null);
            WaitForOperation(context);
            Check(backend.Reads == 1 && workspace.Items.Count == 3);
            Check(workspace.Items.Select(i => i.Name).Distinct().Count() == 1);
            Check(workspace.Items.Select(i => i.Id).Distinct().Count() == 3);
            Check(workspace.Result.Contains("HKLM Run (64-bit): access denied"));
            Check(workspace.Items.Any(i => i.Status == "Disabled (StartupApproved)"));
            Check(workspace.Items.Any(i => i.Status == "Unknown (no StartupApproved value)"));
            var disabled = new byte[12]; disabled[0] = 3;
            var enabled = new byte[12]; enabled[0] = 2;
            Check(WindowsStartupAppsBackend.ParseApproval(disabled) == "Disabled (StartupApproved)");
            Check(WindowsStartupAppsBackend.ParseApproval(enabled) == "Enabled (StartupApproved)");
            Check(WindowsStartupAppsBackend.ParseApproval([]) == "Unknown (malformed StartupApproved value)");
            Check(WindowsStartupAppsBackend.ParseApproval(new byte[11]) == "Unknown (malformed StartupApproved value)");
            Check(WindowsStartupAppsBackend.ParseApproval(new byte[13]) == "Unknown (malformed StartupApproved value)");
            Check(WindowsStartupAppsBackend.ParseApproval(new byte[12]) == "Unknown (StartupApproved value)");
        });
        run("Typed registry identities detect stale source, view, type and raw values", () =>
        {
            var entry = RegistryIdentity(StartupHive.LocalMachine, StartupView.Registry32, "Updater", StartupRegistryKind.DWord, "2");
            Check(StartupAppsService.MatchesRegistryValue(entry, StartupHive.LocalMachine, StartupView.Registry32, "Updater", StartupRegistryKind.DWord, "2"));
            Check(!StartupAppsService.MatchesRegistryValue(entry, StartupHive.CurrentUser, StartupView.Registry32, "Updater", StartupRegistryKind.DWord, "2"));
            Check(!StartupAppsService.MatchesRegistryValue(entry, StartupHive.LocalMachine, StartupView.Registry64, "Updater", StartupRegistryKind.DWord, "2"));
            Check(!StartupAppsService.MatchesRegistryValue(entry, StartupHive.LocalMachine, StartupView.Registry32, "Updater", StartupRegistryKind.String, "2"));
            Check(!StartupAppsService.MatchesRegistryValue(entry, StartupHive.LocalMachine, StartupView.Registry32, "Updater", StartupRegistryKind.DWord, "3"));
            var backup = System.Text.Json.JsonDocument.Parse(StartupAppsService.RegistryBackupJson(entry));
            Check(backup.RootElement.GetProperty("RegistryKind").GetString() == "DWord" && backup.RootElement.GetProperty("RawValue").GetString() == "2");
        });
        run("Startup commands validate the executable and quote paths with spaces", () =>
        {
            var root = Path.Combine(artifacts, "startup-apps"); Directory.CreateDirectory(root);
            var exe = Path.Combine(root, "My Tool.exe"); File.WriteAllText(exe, "fixture, never executed");
            Check(StartupAppsService.BuildCommand(exe, "--quiet") == "\"" + exe + "\" --quiet");
            Throws<ArgumentException>(() => StartupAppsService.ValidateExecutable(Path.Combine(root, "missing.exe")));
            Throws<ArgumentException>(() => StartupAppsService.ValidateExecutable("relative.exe"));
            Throws<ArgumentException>(() => StartupAppsService.BuildCommand(exe, "--bad\nargument"));
            Throws<ArgumentException>(() => StartupAppsService.BuildCommand(exe, new string('x', 261)));
        });
        run("Startup value naming checks case-insensitive Run and StartupApproved collisions", () =>
        {
            Check(WindowsStartupAppsBackend.ChooseUniqueName("Editor", ["editor", "EDITOR (2)"]) == "Editor (3)");
            var externalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Editor (2)"] = "external command" };
            Throws<IOException>(() => WindowsStartupAppsBackend.VerifyWrite("Editor (2)", "expected", _ => { }, name => externalValues[name]));
            Check(externalValues["Editor (2)"] == "external command");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            WindowsStartupAppsBackend.VerifyWrite("Editor (3)", "expected", name => values[name] = "expected", name => values[name]);
            Check(values["Editor (3)"] == "expected");

            Throws<IOException>(() => WindowsStartupAppsBackend.VerifyWrite("Editor (4)", "expected", name => values[name] = "written", _ => throw new UnauthorizedAccessException("read denied")));
            Check(values["Editor (4)"] == "written");
        });
        run("Startup page wiring uses an injectable backend and exposes scoped actions", () =>
        {
            var backend = new FakeBackend();
            var context = new TestContext(Path.Combine(artifacts, "startup-page-data"));
            var page = new StartupAppsModule(backend).CreatePage(context);
            Check(page.Id == "startup-apps" && page.Group == "Windows" && page.HasStartupAppsWorkspace);
            Check(page.Actions.Select(a => a.Title).SequenceEqual(new[] { "Refresh startup apps", "Choose .exe", "Add to my startup", "Remove from startup", "Open Windows startup settings" }));
            Check(backend.Reads == 0 && backend.Adds == 0);
            var w = page.StartupAppsWorkspace!;
            w.SelectedExecutable = Path.Combine(artifacts, "startup-apps", "My Tool.exe");
            w.AppName = "Editor";
            w.Arguments = "--quiet";
            w.AddAction!.Execute(null);
            WaitForOperation(context);
            Check(backend.Adds == 1 && backend.LastCommand == "\"" + w.SelectedExecutable + "\" --quiet");
            Check(w.IsInitialized && w.Items.Count == 1 && w.Items[0].Source == "HKCU Run (64-bit)");
            Check(w.Result.Contains("was not run", StringComparison.OrdinalIgnoreCase));
        });
        run("Removal confirmation is required and removal targets only the selected typed identity", () =>
        {
            var user = RegistryIdentity(StartupHive.CurrentUser, StartupView.Registry64, "Duplicate", StartupRegistryKind.String, "\"C:\\User.exe\"");
            var machine = RegistryIdentity(StartupHive.LocalMachine, StartupView.Registry64, "Duplicate", StartupRegistryKind.ExpandString, "\"%ProgramFiles%\\Machine.exe\"");
            var other = RegistryIdentity(StartupHive.LocalMachine, StartupView.Registry32, "Duplicate", StartupRegistryKind.String, "\"C:\\Other.exe\"");
            var backend = new FakeBackend { Snapshot = new([
                new("Duplicate", "HKCU Run (64-bit)", "Current user", "\"C:\\User.exe\"", "Unknown", user),
                new("Duplicate", "HKLM Run (64-bit)", "All users", "\"%ProgramFiles%\\Machine.exe\"", "Enabled", machine),
                new("Duplicate", "HKLM Run (32-bit)", "All users", "\"C:\\Other.exe\"", "Unknown", other)
            ], []) };
            var context = new TestContext(Path.Combine(artifacts, "startup-remove-data"));
            var workspace = new StartupAppsModule(backend).CreatePage(context).StartupAppsWorkspace!;
            workspace.RefreshAction!.Execute(null); WaitForOperation(context);
            Check(workspace.Items.Select(i => i.Id).Distinct().Count() == 3);
            var selected = workspace.Items.Single(i => i.Identity == machine);
            workspace.SelectedItem = selected;
            Check(workspace.RemoveAction!.IsEnabled);
            context.Confirm = false;
            workspace.RemoveAction.Execute(null); WaitForOperation(context);
            Check(backend.Removes == 0 && backend.RemovalAttempts == 0 && workspace.Items.Count == 3);
            Check(context.LastConfirmation.Contains("all-users startup entry") && context.LastConfirmation.Contains("StartupApproved"));
            context.Confirm = true;
            workspace.SelectedItem = workspace.Items.Single(i => i.Identity == machine);
            workspace.RemoveAction.Execute(null); WaitForOperation(context);
            Check(backend.Removes == 1 && backend.LastRemoved == machine);
            Check(workspace.Items.Count == 2 && workspace.Items.All(i => i.Identity != machine));
            Check(workspace.SelectedItem is null && !workspace.RemoveAction.IsEnabled);
            Check(workspace.Result.Contains("app remains installed") && workspace.Result.Contains("backup.json"));
        });
        run("Stale registrations are preserved and Startup files are archived with shortcut targets intact", () =>
        {
            var identity = RegistryIdentity(StartupHive.CurrentUser, StartupView.Registry64, "Stale", StartupRegistryKind.String, "old");
            var backend = new FakeBackend { Snapshot = new([new("Stale", "HKCU Run", "Current user", "old", "Unknown", identity)], []) };
            var context = new TestContext(Path.Combine(artifacts, "startup-stale-data"));
            var workspace = new StartupAppsModule(backend).CreatePage(context).StartupAppsWorkspace!;
            workspace.RefreshAction!.Execute(null); WaitForOperation(context);
            workspace.SelectedItem = workspace.Items[0];
            backend.Snapshot = new([new("Stale", "HKCU Run", "Current user", "new", "Unknown", RegistryIdentity(StartupHive.CurrentUser, StartupView.Registry64, "Stale", StartupRegistryKind.String, "new"))], []);
            workspace.RemoveAction!.Execute(null); WaitForOperation(context);
            Check(backend.Removes == 0 && backend.Snapshot.Items.Single().Identity.RawValueJson == "new");

            var root = Path.Combine(artifacts, "startup-file-removal", "Startup");
            var data = Path.Combine(artifacts, "startup-file-removal", "Data");
            Directory.CreateDirectory(root); Directory.CreateDirectory(data);
            var shortcut = Path.Combine(root, "Editor.lnk"); File.WriteAllText(shortcut, "shortcut bytes");
            var installed = Path.Combine(artifacts, "startup-file-removal", "Installed"); Directory.CreateDirectory(installed);
            var target = Path.Combine(installed, "Editor.exe"); File.WriteAllText(target, "installed application bytes");
            var fileIdentity = StartupAppsService.CaptureFileIdentity(shortcut, root);
            var removal = StartupAppsService.ArchiveStartupFile(fileIdentity, data);
            Check(!File.Exists(shortcut) && File.Exists(target) && File.ReadAllText(removal.ArchivePath) == "shortcut bytes");
            Check(removal.ArchivePath.StartsWith(Path.Combine(data, "StartupApps", "Removed"), StringComparison.OrdinalIgnoreCase));
            var stale = Path.Combine(root, "Stale.url"); File.WriteAllText(stale, "before");
            var staleIdentity = StartupAppsService.CaptureFileIdentity(stale, root); File.WriteAllText(stale, "after");
            Throws<IOException>(() => StartupAppsService.ArchiveStartupFile(staleIdentity, data));
            Check(File.ReadAllText(stale) == "after");
            var directExe = Path.Combine(root, "Direct.exe"); File.WriteAllText(directExe, "direct startup executable");
            var directResult = StartupAppsService.ArchiveStartupFile(StartupAppsService.CaptureFileIdentity(directExe, root), data);
            Check(!File.Exists(directExe) && File.ReadAllText(directResult.ArchivePath) == "direct startup executable");
            Throws<IOException>(() => StartupAppsService.CaptureFileIdentity(target, root));
            var blockedData = Path.Combine(artifacts, "startup-file-removal", "Blocked"); Directory.CreateDirectory(blockedData);
            File.WriteAllText(Path.Combine(blockedData, "StartupApps"), "not a directory");
            Throws<IOException>(() => StartupAppsService.ArchiveStartupFile(StartupAppsService.CaptureFileIdentity(stale, root), blockedData));
            Check(File.Exists(stale));
        });
        run("Browse selects a validated executable and a declined add never calls the backend", () =>
        {
            var path = Path.Combine(artifacts, "startup-apps", "My Tool.exe");
            var backend = new FakeBackend();
            var context = new TestContext(Path.Combine(artifacts, "startup-browse-data")) { PickedFile = path };
            var w = new StartupAppsModule(backend).CreatePage(context).StartupAppsWorkspace!;
            w.BrowseAction!.Execute(null);
            WaitForOperation(context);
            Check(w.SelectedExecutable == path && w.AppName == "My Tool");
            context.Confirm = false;
            w.AddAction!.Execute(null);
            WaitForOperation(context);
            Check(backend.Adds == 0 && context.LastConfirmation.Contains("will not be run"));
        });
    }

    private sealed class FakeBackend : IStartupAppsBackend
    {
        public int Reads, Adds;
        public string LastCommand = "";
        public StartupReadResult Snapshot { get; set; } = new([new StartupCandidate("Editor", "HKCU Run (64-bit)", "Current user", "", "Unknown (no StartupApproved value)", RegistryIdentity(StartupHive.CurrentUser, StartupView.Registry64, "Editor", StartupRegistryKind.String, "\"C:\\Editor.exe\""))], []);
        public int Removes, RemovalAttempts;
        public StartupEntryIdentity? LastRemoved;
        public StartupReadResult Read()
        {
            Reads++;
            return Snapshot with { Items = Snapshot.Items.Select(item => item with { Command = LastCommand.Length == 0 ? item.Command : LastCommand }).ToArray() };
        }
        public string AddCurrentUser(string name, string executable, string arguments)
        {
            Adds++;
            LastCommand = StartupAppsService.BuildCommand(executable, arguments);
            return name;
        }
        public StartupRemovalResult Remove(StartupEntryIdentity identity, string dataDirectory)
        {
            RemovalAttempts++;
            var current = Snapshot.Items.FirstOrDefault(item => item.Identity.Id == identity.Id);
            if (current is null || current.Identity != identity) throw new IOException("The startup entry changed and was preserved.");
            LastRemoved = identity; Removes++;
            Snapshot = Snapshot with { Items = Snapshot.Items.Where(item => item.Identity.Id != identity.Id).ToArray() };
            return new(Path.Combine(dataDirectory, "StartupApps", "Removed", "backup.json"), identity.Type == StartupEntryType.StartupFile);
        }
    }

    private sealed class TestContext(string directory) : IModuleContext
    {
        private CancellationTokenSource? operation;
        private volatile bool running;
        public bool Confirm = true;
        public string? PickedFile;
        public string LastConfirmation = "";
        public string DataDirectory { get; } = directory;
        public bool IsOperationRunning => running;
        public CancellationToken OperationToken => operation?.Token ?? CancellationToken.None;
        public string Get(string key, string fallback = "") => fallback;
        public void Save(string key, string value) { }
        public void Log(string module, string message) { }
        public Task<bool> ConfirmAsync(string title, string message) { LastConfirmation = message; return Task.FromResult(Confirm); }
        public Task ShowReportAsync(string title, string content) => Task.CompletedTask;
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult(PickedFile);
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) => throw new InvalidOperationException("Tests must not run applications.");
        public void Open(string path) { }
        public bool TryBeginOperation() { if (running) return false; operation = new(); running = true; return true; }
        public void EndOperation() { operation?.Dispose(); operation = null; running = false; }
    }

    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Startup app test assertion failed."); }
    private static StartupEntryIdentity RegistryIdentity(StartupHive hive, StartupView view, string name, StartupRegistryKind kind, string raw) =>
        new(StartupEntryType.RegistryValue, hive, view, name, kind, raw, Scope: hive == StartupHive.LocalMachine ? StartupScope.AllUsers : StartupScope.CurrentUser);
    private static void WaitForOperation(TestContext context)
    {
        if (!SpinWait.SpinUntil(() => !context.IsOperationRunning, TimeSpan.FromSeconds(3)))
            throw new TimeoutException("The fake startup action did not finish.");
    }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
