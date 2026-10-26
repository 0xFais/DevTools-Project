using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DevTools.Contracts;
using DevTools.Modules.Scoop;

namespace DevTools.Tests;

internal static class ScoopTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Scoop assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Unsafe input accepted."); }
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Scoop PowerShell output stays readable text across host, warning and error streams", () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            ScoopService.RunScript("Write-Output 'normal output'; Write-Host 'host message'; Write-Warning 'warning message'; [Console]::Error.WriteLine('native error')", CancellationToken.None, new InlineProgress(lines.Enqueue)).GetAwaiter().GetResult();
            var output = string.Join("\n", lines); Check(output.Contains("normal output") && output.Contains("host message") && output.Contains("warning message") && output.Contains("native error") && !output.Contains("<Objs") && !output.Contains("CLIXML"));
        });
        run("Temporary Scoop Git trust preserves inherited config and stays inside the child process", () =>
        {
            var root = Path.Combine(artifacts, "repository's folder"); var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var original = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            var script = "$env:GIT_CONFIG_COUNT='1'; $env:GIT_CONFIG_KEY_0='user.name'; $env:GIT_CONFIG_VALUE_0='Existing user'; " + ScoopService.WithGitTrust("Write-Output ($env:GIT_CONFIG_COUNT+'|'+$env:GIT_CONFIG_KEY_1+'|'+$env:GIT_CONFIG_VALUE_1+'|'+$env:GIT_CONFIG_VALUE_0)", [root]);
            ScoopService.RunScript(script, CancellationToken.None, new InlineProgress(lines.Enqueue)).GetAwaiter().GetResult();
            Check(string.Join("\n", lines).Contains("2|safe.directory|" + root.Replace('\\', '/') + "|Existing user") && Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT") == original);
            Reject(() => ScoopService.WithGitTrust("", ["*"])); Reject(() => ScoopService.WithGitTrust("", ["relative"]));
        });
        run("Scoop ownership preflight requests permission before running the command", () =>
        {
            var f = new Fixture(artifacts); Directory.CreateDirectory(Path.Combine(f.Root, "buckets", "main", ".git"));
            f.Context.Process = (_, _, _) => Task.FromResult(new ProcessResult(128, "fatal: detected dubious ownership in repository"));
            var trustFile = Path.Combine(Path.GetDirectoryName(f.Root)!, "trust.txt"); File.AppendAllText(f.Script, "\n$env:GIT_CONFIG_COUNT | Set-Content -LiteralPath " + ScoopService.Literal(trustFile));
            Act(f, "UpdateBuckets");
            Check(f.Page.Status.StartsWith("Completed") && f.Context.Preview.Contains("Trust is limited to this child process") && File.Exists(trustFile) && int.Parse(File.ReadAllText(trustFile)) >= 1);
        });
        run("Declining repository trust does not run Scoop or change Git settings", () =>
        {
            var f = new Fixture(artifacts); Directory.CreateDirectory(Path.Combine(f.Root, "buckets", "main", ".git")); f.Context.Confirm = false;
            f.Context.Process = (_, _, _) => Task.FromResult(new ProcessResult(128, "fatal: detected dubious ownership in repository"));
            Act(f, "UpdateBuckets"); Check(f.Page.Status.StartsWith("Declined") && !File.Exists(f.Calls));
        });
        run("Modern Scoop metadata takes priority over legacy files for details and holds", () =>
        {
            var f = new Fixture(artifacts); var current = Path.Combine(f.Root, "apps", "alpha", "current");
            File.WriteAllText(Path.Combine(current, "scoop-manifest.json"), "{\"version\":\"2.0\",\"description\":\"Modern app\",\"bin\":\"bin/tool.exe\"}");
            File.WriteAllText(Path.Combine(current, "scoop-install.json"), "{\"bucket\":\"main\",\"hold\":false}");
            var app = ScoopService.Installed(f.Root, CancellationToken.None).Single(a => a.Name == "alpha");
            Check(app.Version == "2.0" && app.Description == "Modern app" && !app.Held && app.ManifestPath.EndsWith("scoop-manifest.json"));
        });
        run("Hold and Unhold refresh the modern install metadata after real child commands", () =>
        {
            var f = new Fixture(artifacts);
            File.AppendAllText(f.Script, "\nif ($args[0] -in @('hold','unhold')) { for ($i=1; $i -lt $args.Count; $i++) { $dir=Join-Path $env:SCOOP ('apps/' + $args[$i] + '/current'); @{bucket='main';hold=($args[0] -eq 'hold')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'scoop-install.json') } }\n");
            Act(f, "Refresh"); f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "alpha");
            Act(f, "unhold"); Check(f.Page.Status.StartsWith("Completed") && !f.Workspace.Installed.Single(a => a.Name == "alpha").Held);
            Act(f, "hold"); Check(f.Page.Status.StartsWith("Completed") && f.Workspace.Installed.Single(a => a.Name == "alpha").Held);
        });
        run("Hold failures are reported when Scoop does not change the saved hold state", () =>
        {
            var f = new Fixture(artifacts); Act(f, "Refresh"); f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "alpha"); Act(f, "unhold"); Check(f.Page.Status.StartsWith("Failed"));
        });
        run("Icon discovery finds executables inside nested app folders", () =>
        {
            var f = new Fixture(artifacts); var app = Path.Combine(f.Root, "apps", "alpha"); var nested = Path.Combine(app, "current", "tools", "bin"); Directory.CreateDirectory(nested);
            var exe = Path.Combine(nested, "alpha.exe"); File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), exe);
            Check(ScoopService.IconCandidates(app).Contains(exe));
            var converter = new DevTools.App.Views.ScoopIconConverter(); Check(converter.Convert(app, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture) is System.Windows.Media.Imaging.BitmapSource);
        });
        run("Scoop icon loading returns immediately and ignores recycled rows", () =>
        {
            var f = new Fixture(artifacts); var app = Path.Combine(f.Root, "apps", "alpha");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(app, "current", "alpha.exe"));
            var image = new System.Windows.Controls.Image();
            DevTools.App.Views.ScoopAsyncIcon.SetDirectory(image, app);
            Check(image.Source is null); // no extraction occurs inside the binding callback
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (image.Source is null && DateTime.UtcNow < deadline)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }
            Check(image.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true });
            DevTools.App.Views.ScoopAsyncIcon.SetDirectory(image, app + "-missing");
            DevTools.App.Views.ScoopAsyncIcon.SetDirectory(image, null);
            var until = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < until) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(10); }
            Check(image.Source is null);
        });
        run("Scoop itself reflects its time-limited hold configuration", () =>
        {
            var f = new Fixture(artifacts); File.WriteAllText(Path.Combine(f.Root, "config.json"), JsonSerializer.Serialize(new { HOLD_UPDATE_UNTIL = DateTimeOffset.Now.AddDays(1) }));
            Check(ScoopService.Installed(f.Root, CancellationToken.None).Single(a => a.Name == "scoop").Held);
        });
        run("Scoop root detection, metadata, holds and bucket version comparison", () =>
        {
            var f = new Fixture(artifacts); var apps = ScoopService.Installed(f.Root, CancellationToken.None);
            Check(ScoopService.DetectRoot(f.Root) == f.Root && apps.Single(a => a.Name == "alpha").Held);
            Check(apps.Single(a => a.Name == "alpha").Status == "Update: 2.0");
            Check(ScoopService.Buckets(f.Root).Single().Manifests == 2);
        });
        run("Scoop search parses real JSON and reports installed state", () =>
        {
            var f = new Fixture(artifacts); var apps = ScoopService.Search(f.Root, "ALP", CancellationToken.None);
            Check(apps.Single().Name == "alpha" && apps[0].Status == "Installed" && apps[0].Description == "Quoted \"app\"");
        });
        run("Scoop path traversal, invalid folder names and PowerShell quoting", () =>
        {
            var f = new Fixture(artifacts);
            foreach (var name in new[] { "..", "../escape", "CON", "NUL.txt", "x.", "bad:name", "bad/name" }) Reject(() => ScoopService.SafeSegment(name));
            Reject(() => ScoopService.Within(f.Root, Path.Combine(f.Root, "..", "escape.exe")));
            Check(ScoopService.Literal("a';& $x") == "'a'';& $x'");
            Check(ScoopService.Script(f.Root, ["info", "main/alpha; Write-Output 'bad'"]).Contains("'main/alpha; Write-Output ''bad'''"));
        });
        run("Scoop custom manifests preserve Unicode and escaped names", () =>
        {
            var json = ScoopService.CustomManifest("1.0", "وصف \"quoted\"", "bin/app.exe", "myapp", "My App");
            using var parsed = JsonDocument.Parse(json.ToJsonString());
            Check(parsed.RootElement.GetProperty("bin")[0][1].GetString() == "myapp");
            Check(parsed.RootElement.GetProperty("description").GetString() == "وصف \"quoted\"");
        });
        run("Scoop command passes literal arguments and detects failures", () =>
        {
            var f = new Fixture(artifacts); var lines = new List<string>();
            ScoopService.RunScript(ScoopService.Script(f.Root, ["info", "space ' ; $literal"]), CancellationToken.None, new InlineProgress(lines.Add)).GetAwaiter().GetResult();
            Check(JsonSerializer.Deserialize<string[]>(File.ReadAllLines(f.Calls).Single())![1] == "space ' ; $literal");
            var failed = false;
            try { ScoopService.RunScript("exit 7", CancellationToken.None, new InlineProgress(lines.Add)).GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
            Check(failed);
        });
        run("Scoop process cancellation terminates PowerShell", () =>
        {
            using var cancel = new CancellationTokenSource(300); var cancelled = false;
            try { ScoopService.RunScript("Start-Sleep -Seconds 30", cancel.Token, new InlineProgress(_ => { })).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);
        });
        run("Scoop compact filter includes held status and preserves all actions", () =>
        {
            var f = new Fixture(artifacts); Act(f, "Refresh"); f.Workspace.Filter = "held";
            Check(f.Workspace.Installed.Single().Name == "alpha");
            foreach (var key in new[] { "update", "uninstall", "hold", "unhold", "reset", "Status", "UpdateAll", "ResetAll", "Cleanup", "Install", "Info", "Details", "SwitchVersion", "AddBucket", "RemoveBucket", "UpdateBuckets", "ShowCache", "ClearCache", "AddCustom", "SavePaths", "SyncPaths", "ImportRules" }) Check(f.Workspace.Commands.ContainsKey(key));
            Check(f.Page.Group == "Apps Manager" && f.Workspace.Field("method").Value == "Copy");
        });
        foreach (var move in new[] { false, true }) run("Scoop custom " + (move ? "Move" : "Copy") + " registers manifest, junction and reset", () =>
        {
            var f = new Fixture(artifacts); var source = f.Source(); f.SetCustom(source, move); Act(f, "AddCustom");
            Check(!f.Page.Status.StartsWith("Failed"));
            Check(File.Exists(Path.Combine(f.Root, "apps", "custom-test", "current", "tool.exe")));
            Check(Directory.Exists(source) == !move && File.ReadAllText(f.Calls).Contains("reset"));
        });
        run("Scoop rejected custom source leaves original untouched", () =>
        {
            var f = new Fixture(artifacts); var source = f.Source(); f.SetCustom(source, false);
            f.Workspace.Field("exe").Value = Path.Combine(f.Root, "outside.exe"); Act(f, "AddCustom");
            Check(f.Page.Status.StartsWith("Failed") && File.Exists(Path.Combine(source, "tool.exe")) && !Directory.Exists(Path.Combine(f.Root, "apps", "custom-test")));
        });
        run("Scoop declined Move leaves source and destination unchanged", () =>
        {
            var f = new Fixture(artifacts); var source = f.Source(); f.SetCustom(source, true); f.Context.Confirm = false; Act(f, "AddCustom");
            Check(Directory.Exists(source) && !Directory.Exists(Path.Combine(f.Root, "apps", "custom-test")));
        });
        run("Scoop reset failure retains registered custom app for repair", () =>
        {
            var f = new Fixture(artifacts); File.AppendAllText(f.Script, "\nif ($args[0] -eq 'reset') { exit 9 }\n");
            var source = f.Source(); f.SetCustom(source, false); Act(f, "AddCustom");
            Check(f.Page.Status.StartsWith("Failed") && Directory.Exists(source) && File.Exists(Path.Combine(f.Root, "apps", "custom-test", "current", "manifest.json")));
        });
        run("Scoop queue captures package selection at click time", () =>
        {
            var f = new Fixture(artifacts); Act(f, "Refresh");
            f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "alpha");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            f.Workspace.Commands["Status"].Execute(null);
            f.Workspace.Commands["hold"].Execute(null);
            f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "scoop");
            Drain(f.Context);
            Check(File.ReadAllText(f.Calls).Contains("[\"hold\",\"alpha\"]"));
            SynchronizationContext.SetSynchronizationContext(null);
        });
        run("Scoop install preview decline does not start installation", () =>
        {
            var f = new Fixture(artifacts); f.Workspace.Query = "alpha"; Act(f, "Search"); f.Workspace.SearchSelection.Add(f.Workspace.Results.Single()); f.Context.Confirm = false; Act(f, "Install");
            Check(!File.Exists(f.Calls) && f.Context.Preview.Contains("Dependencies:") && f.Context.Preview.Contains("License:"));
        });
        run("Scoop cancelled Move restores the original folder before registration", () =>
        {
            var f = new Fixture(artifacts); var source = f.Source(); f.SetCustom(source, true); f.Context.CancelOnConfirm = true; Act(f, "AddCustom");
            Check(f.Page.Status.StartsWith("Cancelled") && File.Exists(Path.Combine(source, "tool.exe")) && !Directory.Exists(Path.Combine(f.Root, "apps", "custom-test")));
        });
        run("Scoop cancelling the active command clears waiting operations", () =>
        {
            var f = new Fixture(artifacts); File.AppendAllText(f.Script, "\nStart-Sleep -Seconds 30\n");
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { f.Workspace.Commands["Status"].Execute(null); f.Workspace.Commands["ShowCache"].Execute(null); f.Context.Cancel(); Drain(f.Context); Check(f.Page.Status.StartsWith("Cancelled") && (!File.Exists(f.Calls) || !File.ReadAllText(f.Calls).Contains("cache"))); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        run("Scoop version switching and bucket commands use selected values", () =>
        {
            var f = new Fixture(artifacts); Directory.CreateDirectory(Path.Combine(f.Root, "apps", "alpha", "0.9")); Act(f, "Refresh");
            f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "alpha"); f.Workspace.Version = "0.9"; Act(f, "SwitchVersion");
            f.Workspace.BucketName = "extras"; f.Workspace.BucketUrl = "https://example.com/bucket.git"; Act(f, "AddBucket");
            var calls = File.ReadAllText(f.Calls); Check(calls.Contains("alpha@0.9") && calls.Contains("https://example.com/bucket.git"));
        });
        run("Scoop search truncates large result sets at 500", () =>
        {
            var f = new Fixture(artifacts); var bucket = Path.Combine(f.Root, "buckets", "main", "bucket");
            for (var i = 0; i < 505; i++) File.WriteAllText(Path.Combine(bucket, "test-" + i + ".json"), "{\"version\":\"1\"}");
            Check(ScoopService.Search(f.Root, "test-", CancellationToken.None).Count == 500);
        });
        run("Scoop relative PATH rules persist without changing unrelated manifest data", () =>
        {
            var f = new Fixture(artifacts); Act(f, "Refresh"); f.Workspace.Selected = f.Workspace.Installed.Single(a => a.Name == "alpha"); f.Workspace.PathEntries = "."; Act(f, "SavePaths");
            if (f.Page.Status.StartsWith("Failed")) throw new Exception(f.Page.Status); Check(File.Exists(Path.Combine(f.Context.DataDirectory, "scoop-paths.json")));
            var m = ScoopService.Read(f.Workspace.Selected!.ManifestPath); Check(ScoopService.Text(m["description"]) == "Quoted \"app\"" && ScoopService.Strings(m["env_add_path"]).Single() == ".");
        });
    }
    internal static ModulePage Preview(string artifacts)
    {
        var f = new Fixture(artifacts); Replace(f.Workspace.Installed, ScoopService.Installed(f.Root, CancellationToken.None));
        f.Workspace.Installed.Add(new("vscode", "1.95.3", "extras", false, "Up to date", "", ""));
        f.Workspace.SetupState = ScoopSetupState.Ready; f.Workspace.SetupMessage = "Installed at " + f.Root + " · Available in your user PATH";
        f.Workspace.Summary = "3 installed · 1 update · 1 held · 1 bucket"; return f.Page;
    }
    private static void Replace<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> rows) { target.Clear(); foreach (var row in rows) target.Add(row); }
    private static void Act(Fixture f, string key)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { f.Workspace.Commands[key].Execute(null); Drain(f.Context); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Drain(TestContext c)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (c.IsOperationRunning && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
        Check(!c.IsOperationRunning);
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class Fixture
    {
        internal string Root, Calls, Script;
        internal TestContext Context;
        internal ModulePage Page;
        internal ScoopWorkspace Workspace => Page.ScoopWorkspace!;
        internal Fixture(string artifacts)
        {
            var folder = Path.Combine(artifacts, "scoop-" + Guid.NewGuid().ToString("N")); Root = Path.Combine(folder, "Scoop root's"); Calls = Path.Combine(folder, "calls.jsonl");
            Script = Path.Combine(Root, "apps", "scoop", "current", "bin", "scoop.ps1"); Directory.CreateDirectory(Path.GetDirectoryName(Script)!);
            Directory.CreateDirectory(Path.Combine(Root, "shims")); File.WriteAllText(Path.Combine(Root, "shims", "scoop.cmd"), "fixture");
            File.WriteAllText(Script, "$args | ConvertTo-Json -Compress | Add-Content -LiteralPath " + ScoopService.Literal(Calls) + "\nWrite-Output 'fixture output'\n");
            var installed = Path.Combine(Root, "apps", "alpha", "current"); Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "manifest.json"), "{\"version\":\"1.0\",\"description\":\"Quoted \\\"app\\\"\",\"license\":{\"identifier\":\"MIT\"}}");
            File.WriteAllText(Path.Combine(installed, "install.json"), "{\"bucket\":\"main\",\"hold\":true}");
            var bucket = Path.Combine(Root, "buckets", "main", "bucket"); Directory.CreateDirectory(bucket);
            File.WriteAllText(Path.Combine(bucket, "alpha.json"), "{\"version\":\"2.0\",\"description\":\"Quoted \\\"app\\\"\",\"license\":\"MIT\",\"depends\":[\"git\"],\"url\":\"https://example.com/a.zip\"}");
            File.WriteAllText(Path.Combine(bucket, "beta.json"), "{\"version\":\"1.0\"}");
            Context = new(Path.Combine(folder, "data")); Context.Save("scoop.root", Root);
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { Page = new ScoopModule(new ScoopSetupTests.FakeBackend { UserPath = Path.Combine(Root, "shims") }).CreatePage(Context); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        internal string Source() { var path = Path.Combine(Path.GetDirectoryName(Root)!, "portable app"); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "tool.exe"), "fixture"); return path; }
        internal void SetCustom(string source, bool move) { Workspace.Field("name").Value = "custom-test"; Workspace.Field("folder").Value = source; Workspace.Field("exe").Value = Path.Combine(source, "tool.exe"); Workspace.Field("method").Value = move ? "Move" : "Copy"; }
    }
    internal sealed class TestContext : IModuleContext
    {
        private readonly Dictionary<string, string> settings = [];
        private CancellationTokenSource? operation;
        internal bool Confirm = true;
        internal bool CancelOnConfirm;
        internal string Preview = "";
        internal Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>>? Process;
        public TestContext(string directory) { DataDirectory = directory; Directory.CreateDirectory(directory); }
        public string DataDirectory { get; }
        public bool IsOperationRunning => operation is not null;
        public CancellationToken OperationToken => operation?.Token ?? CancellationToken.None;
        public string Get(string key, string fallback = "") => settings.GetValueOrDefault(key, fallback);
        public void Save(string key, string value) => settings[key] = value;
        public void Log(string module, string message) { }
        public Task<bool> ConfirmAsync(string title, string message) { Preview = message; if (CancelOnConfirm) Cancel(); return Task.FromResult(Confirm); }
        internal void Cancel() => operation?.Cancel();
        public Task ShowReportAsync(string title, string content) => Task.CompletedTask;
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) => Process?.Invoke(executable, arguments, cancellationToken) ?? throw new NotSupportedException();
        public void Open(string path) { }
        public bool TryBeginOperation() { if (operation is not null) return false; operation = new(); return true; }
        public void EndOperation() { operation?.Dispose(); operation = null; }
    }
}
