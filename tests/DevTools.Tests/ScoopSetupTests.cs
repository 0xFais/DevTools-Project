using System.IO;
using DevTools.Contracts;
using DevTools.Modules.Scoop;

namespace DevTools.Tests;

internal static class ScoopSetupTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Scoop setup assertion failed."); }
    private static string Folder(string artifacts) => Path.Combine(artifacts, "setup-" + Guid.NewGuid().ToString("N"));
    private static void Seed(string root) { var bin = Path.Combine(root, "apps", "scoop", "current", "bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "scoop.ps1"), "# fixture"); Directory.CreateDirectory(Path.Combine(root, "shims")); File.WriteAllText(Path.Combine(root, "shims", "scoop.cmd"), "fixture"); }
    private static readonly IProgress<string> Log = new Progress<string>(_ => { });
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Scoop setup distinguishes missing, partial, missing PATH and ready states without writes", () =>
        {
            var root = Folder(artifacts); var backend = new FakeBackend(); var setup = new ScoopSetupService(backend);
            Check(setup.Probe("").State == ScoopSetupState.Missing);
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "partial.txt"), "keep"); Check(setup.Probe(root).State == ScoopSetupState.Broken);
            Seed(root); Check(setup.Probe(root).State == ScoopSetupState.PathMissing);
            backend.UserPath = Path.Combine(root, "shims"); var writes = backend.Writes; Check(setup.Probe(root).State == ScoopSetupState.Ready && backend.Writes == writes);
        });
        run("Scoop PATH matching handles quotes, variables, case and trailing separators", () =>
        {
            var path = Path.Combine(artifacts, "Shims");
            Check(ScoopSetupService.ContainsPath("C:\\Keep;\"" + path.ToUpperInvariant() + "\\\"", path));
            Check(!ScoopSetupService.ContainsPath(path + "-other", path));
            Check(ScoopSetupService.AppendPath("C:\\Keep", path) == "C:\\Keep;" + path);
        });
        run("Add Scoop to PATH preserves entries, updates the process and does not duplicate on retry", () =>
        {
            var root = Folder(artifacts); Seed(root); var backend = new FakeBackend { UserPath = "C:\\Keep;C:\\Other", ProcessPath = "C:\\Windows" }; var setup = new ScoopSetupService(backend); var backups = new List<string>();
            setup.AddToPath(root, backups.Add, CancellationToken.None, Log).GetAwaiter().GetResult();
            var after = backend.UserPath; var writes = backend.Writes;
            setup.AddToPath(root, backups.Add, CancellationToken.None, Log).GetAwaiter().GetResult();
            Check(after == "C:\\Keep;C:\\Other;" + Path.Combine(root, "shims") && backend.UserPath == after && backend.Writes == writes);
            Check(backups.Single() == "C:\\Keep;C:\\Other" && backend.Broadcasts == 1 && ScoopSetupService.ContainsPath(backend.ProcessPath, Path.Combine(root, "shims")));
        });
        run("Missing Scoop launcher cannot be added to PATH", () =>
        {
            var root = Folder(artifacts); Seed(root); File.Delete(Path.Combine(root, "shims", "scoop.cmd")); var backend = new FakeBackend(); var failed = false;
            try { new ScoopSetupService(backend).AddToPath(root, _ => { }, CancellationToken.None, Log).GetAwaiter().GetResult(); } catch (InvalidOperationException) { failed = true; }
            Check(failed && backend.Writes == 0);
        });
        run("Installer rejects nonempty, relative and drive-root destinations", () =>
        {
            var root = Folder(artifacts); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "keep.txt"), "untouched");
            foreach (var path in new[] { root, "relative", Path.GetPathRoot(root)!, root + ";bad" })
            {
                var rejected = false; try { ScoopSetupService.ValidateDestination(path); } catch (Exception e) when (e is IOException or ArgumentException) { rejected = true; } Check(rejected);
            }
            Check(File.ReadAllText(Path.Combine(root, "keep.txt")) == "untouched");
        });
        run("Installer uses official script, custom location and scoped administrator option", () =>
        {
            var script = ScoopSetupService.InstallScript(@"C:\Downloads\installer's.ps1", @"C:\Scoop's folder");
            Check(script.Contains("'installer") == false && script.Contains("installer''s.ps1") && script.Contains("-ScoopDir 'C:\\Scoop''s folder' -RunAsAdmin"));
            Check(ScoopSetupService.InstallerUrl == "https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1");
        });
        run("Scoop installation verifies executable and PATH before reporting ready", () =>
        {
            var root = Folder(artifacts); var data = Folder(artifacts); var backend = new FakeBackend { InstallRoot = root, UserPath = "C:\\Keep" }; var setup = new ScoopSetupService(backend);
            setup.Install(root, data, _ => { }, CancellationToken.None, Log).GetAwaiter().GetResult();
            Check(setup.Probe(root).State == ScoopSetupState.Ready && backend.Downloads == 1 && backend.Scripts.Count == 2);
            Check(!Directory.EnumerateFiles(data, "*.ps1").Any() && backend.UserPath.StartsWith("C:\\Keep;"));
        });
        run("Cancelled Scoop download launches no installer and leaves existing PATH unchanged", () =>
        {
            var root = Folder(artifacts); var data = Folder(artifacts); var backend = new FakeBackend { InstallRoot = root, CancelDownload = true, UserPath = "C:\\Keep" }; var cancelled = false;
            try { new ScoopSetupService(backend).Install(root, data, _ => { }, CancellationToken.None, Log).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && backend.Scripts.Count == 0 && backend.UserPath == "C:\\Keep" && !Directory.Exists(root) && !Directory.EnumerateFiles(data).Any());
        });
        run("Failed Scoop installation does not falsely report readiness or erase partial files", () =>
        {
            var root = Folder(artifacts); var backend = new FakeBackend { InstallRoot = root, FailInstall = true }; var setup = new ScoopSetupService(backend); var failed = false;
            try { setup.Install(root, Folder(artifacts), _ => { }, CancellationToken.None, Log).GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
            Check(failed && setup.Probe(root).State == ScoopSetupState.Broken && File.Exists(Path.Combine(root, "partial.txt")));
        });
        run("Setup UI state exposes only the actions appropriate to each installation state", () =>
        {
            var w = new ScoopWorkspace { SetupState = ScoopSetupState.Missing }; Check(w.ShowInstall && !w.HasInstallation && !w.NeedsPath);
            w.SetupState = ScoopSetupState.PathMissing; Check(w.HasInstallation && w.NeedsPath && !w.ShowInstall);
            w.SetupState = ScoopSetupState.Ready; Check(w.HasInstallation && !w.NeedsPath && !w.ShowInstall);
            w.SetupState = ScoopSetupState.Broken; Check(!w.HasInstallation && !w.ShowInstall);
        });
    }
    internal sealed class FakeBackend : IScoopSetupBackend
    {
        private string userPath = "";
        public string UserPath { get => userPath; set { userPath = value; Writes++; } }
        public string ProcessPath { get; set; } = "";
        internal int Writes, Broadcasts, Downloads;
        internal string InstallRoot = "";
        internal bool CancelDownload, FailInstall;
        internal List<string> Scripts = [];
        public void Broadcast() => Broadcasts++;
        public Task DownloadInstaller(string destination, CancellationToken ct) { Downloads++; File.WriteAllText(destination, "# fake installer"); if (CancelDownload) throw new OperationCanceledException(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task Run(string script, CancellationToken ct, IProgress<string> log)
        {
            ct.ThrowIfCancellationRequested(); Scripts.Add(script);
            if (script.Contains("-ScoopDir")) { if (FailInstall) { Directory.CreateDirectory(InstallRoot); File.WriteAllText(Path.Combine(InstallRoot, "partial.txt"), "keep"); throw new IOException("fixture installer failure"); } Seed(InstallRoot); }
            return Task.CompletedTask;
        }
    }
}
