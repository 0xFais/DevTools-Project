using System.IO;
using System.Text.Json;
using DevTools.App;
using DevTools.Infrastructure;
using DevTools.Modules.Windows;

namespace DevTools.Tests;

internal static class PortableStorageTests
{
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Portable settings, activity and machine backups stay below the app folder", () =>
        {
            var app = NewFolder(artifacts); var storage = new PortableStorage(app, "PC-A:user-A"); var context = new AppModuleContext(storage);
            context.Save("settings.theme", "Light"); context.Save("links.previous.test", "original command"); context.Log("Portable", "test");
            Check(File.Exists(Path.Combine(app, "Data", "settings.json")) && context.DataDirectory.StartsWith(Path.Combine(app, "Data", "Machines")));
            Check(context.SettingsDirectory == Path.Combine(app, "Data") && context.LogsDirectory == Path.Combine(app, "Data", "Logs"));
            Check(File.ReadAllText(Path.Combine(context.LogsDirectory, "activity.log")).Contains("test") && !File.Exists(Path.Combine(context.DataDirectory, "activity.log")));
            var recovery = new AppModuleContext(context.DataDirectory);
            Check(recovery.SettingsDirectory == context.SettingsDirectory && recovery.LogsDirectory == context.LogsDirectory);
            Check(new AppModuleContext(new PortableStorage(app, "PC-A:user-A")).Get("settings.theme") == "Light");
        });
        run("Portable UI preferences travel while Windows ownership settings stay with each PC and user", () =>
        {
            var app = NewFolder(artifacts); var a = new AppModuleContext(new PortableStorage(app, "PC-A:user-A"));
            a.Save("settings.theme", "Light"); a.Save("navigation.expanded.windows", "false"); a.Save("links.owned.test", "PC-A command"); a.Save("scoop.pathBackup", "PC-A PATH");
            var b = new AppModuleContext(new PortableStorage(app, "PC-B:user-A")); var c = new AppModuleContext(new PortableStorage(app, "PC-A:user-B"));
            Check(b.Get("settings.theme") == "Light" && b.Get("navigation.expanded.windows") == "false");
            Check(b.Get("links.owned.test") == "" && b.Get("scoop.pathBackup") == "" && c.Get("links.owned.test") == "");
            Check(a.DataDirectory != b.DataDirectory && a.DataDirectory != c.DataDirectory);
        });
        run("Portable internal paths follow a moved app folder without rewriting external paths or saved Windows commands", () =>
        {
            var app = NewFolder(artifacts); var storage = new PortableStorage(app, "same-PC"); var a = new AppModuleContext(storage);
            a.Save("scoop.root", Path.Combine(app, "Scoop")); a.Save("context.path.editor", Path.Combine(app, "Scoop", "editor.exe"));
            a.Save("links.path.browser", @"C:\External\browser.exe"); a.Save("links.previous.test", "\"" + Path.Combine(app, "old.exe") + "\" %1");
            var moved = NewFolder(artifacts); CopyFolder(app, moved); var b = new AppModuleContext(new PortableStorage(moved, "same-PC"));
            Check(b.Get("scoop.root") == Path.Combine(moved, "Scoop") && b.Get("context.path.editor") == Path.Combine(moved, "Scoop", "editor.exe"));
            Check(b.Get("links.path.browser") == @"C:\External\browser.exe" && b.Get("links.previous.test") == a.Get("links.previous.test"));
            Check(File.ReadAllText(Path.Combine(storage.DataRoot, "settings.json")).Contains("@portable/Scoop"));
        });
        run("Portable path decoding rejects traversal and absolute path escapes", () =>
        {
            var layout = new PortableStorage(NewFolder(artifacts), "PC");
            Throws<InvalidDataException>(() => layout.DecodePath("scoop.root", "@portable/../outside"));
            Throws<InvalidDataException>(() => layout.DecodePath("scoop.root", "@portable/C:/outside"));
            Check(layout.EncodePath("scoop.root", @"C:\External") == @"C:\External");
        });
        run("Legacy AppData import copies existing settings and backups once without modifying the originals", () =>
        {
            var old = NewFolder(artifacts); var app = NewFolder(artifacts); var settings = new SettingsStore(old);
            settings.Save("settings.theme", "Light"); settings.Save("scoop.pathBackup", "old PATH");
            Directory.CreateDirectory(Path.Combine(old, "StartSearch")); File.WriteAllText(Path.Combine(old, "StartSearch", "backup.json"), "legacy backup");
            Directory.CreateDirectory(Path.Combine(old, "UpdateRecovery")); File.WriteAllText(Path.Combine(old, "UpdateRecovery", "old.exe"), "old helper");
            var before = File.ReadAllText(Path.Combine(old, "settings.json")); var layout = new PortableStorage(app, "PC-A", old); var context = new AppModuleContext(layout);
            Check(layout.ImportedLegacyData && context.Get("settings.theme") == "Light" && context.Get("scoop.pathBackup") == "old PATH");
            Check(File.ReadAllText(Path.Combine(layout.MachineDirectory, "StartSearch", "backup.json")) == "legacy backup" && !Directory.Exists(Path.Combine(layout.MachineDirectory, "UpdateRecovery")));
            Check(File.ReadAllText(Path.Combine(old, "settings.json")) == before);
            context.Save("settings.theme", "Dark"); Check(!new PortableStorage(app, "PC-A", old).ImportedLegacyData);
            Check(new AppModuleContext(new PortableStorage(app, "PC-B", old)).Get("scoop.pathBackup") == "");
        });
        run("An unwritable portable Data location fails instead of silently creating a roaming profile", () =>
        {
            var app = NewFolder(artifacts); File.WriteAllText(Path.Combine(app, "Data"), "blocked");
            Throws<IOException>(() => new PortableStorage(app, "PC"));
        });
        run("Recovery helper copies the bundled runtime and receives the exact portable machine-data location", () =>
        {
            var app = NewFolder(artifacts); var machine = NewFolder(artifacts);
            foreach (var name in new[] { "DevTools.exe", "DevTools.dll", "DevTools.runtimeconfig.json", "DevTools.deps.json", "DevTools.Contracts.dll", "DevTools.Infrastructure.dll", "DevTools.Modules.dll", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll", "PresentationNative_cor3.dll" }) File.WriteAllText(Path.Combine(app, name), "component");
            Directory.CreateDirectory(Path.Combine(app, "de")); File.WriteAllText(Path.Combine(app, "de", "PresentationFramework.resources.dll"), "resource");
            Directory.CreateDirectory(Path.Combine(app, "Data")); File.WriteAllText(Path.Combine(app, "Data", "private.json"), "not runtime");
            var helper = UpdateResumeScheduler.PrepareHelper(app, machine);
            Check(File.Exists(Path.Combine(helper, "coreclr.dll")) && File.Exists(Path.Combine(helper, "PresentationFramework.dll")) && File.Exists(Path.Combine(helper, "de", "PresentationFramework.resources.dll")) && !Directory.Exists(Path.Combine(helper, "Data")));
            var token = Guid.NewGuid(); var args = UpdateResumeScheduler.RecoveryArguments(token, machine);
            Check(args.Contains(token.ToString("D")) && args.EndsWith("--data-directory \"" + machine + "\""));
            Check(UpdateResumeScheduler.PrepareHelper(helper, machine) == helper);
        });
        run("Portable recovery with no saved plan does not create a helper or register a task", () =>
        {
            var machine = NewFolder(artifacts); new UpdateResumeScheduler(machine).RebindPortableLocationAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(!Directory.Exists(Path.Combine(machine, "UpdateRecovery")) && !File.Exists(Path.Combine(machine, "recovery-location.txt")));
        });
    }
    internal static void Smoke(string output)
    {
        var root = AppContext.BaseDirectory;
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "DevTools.runtimeconfig.json")));
        var bundled = config.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _);
        Check(typeof(object).Assembly.Location.StartsWith(root, StringComparison.OrdinalIgnoreCase) == bundled);
        Check(typeof(System.Windows.Window).Assembly.Location.StartsWith(root, StringComparison.OrdinalIgnoreCase) == bundled);
        var storage = new PortableStorage(root, "portable-runtime-validation"); var context = new AppModuleContext(storage);
        context.Save("settings.theme", "Dark"); context.Save("scoop.root", Path.Combine(root, "Scoop")); context.Log("Portable validation", "Bundled runtime and local data verified.");
        var app = new System.Windows.Application(); app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/DevTools;component/Themes/Studio.xaml", UriKind.Relative) });
        using var shell = new ShellViewModel(context, autoRefresh: false); shell.ApplyTheme();
        var window = new MainWindow { DataContext = shell }; var content = (System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(1180, 820)); content.Arrange(new System.Windows.Rect(0, 0, 1180, 820)); content.UpdateLayout();
        Check(shell.Pages.Count == 14 && shell.Selected.Id == "overview");
        var result = new { RuntimeBundled = bundled, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, CoreRuntime = typeof(object).Assembly.Location, WpfRuntime = typeof(System.Windows.Window).Assembly.Location, storage.DataRoot, context.LogsDirectory, storage.MachineDirectory, Pages = shell.Pages.Count, Passed = true };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        window.Close(); Console.WriteLine("PASS: native app host used the " + (bundled ? "bundled" : "installed") + " CoreCLR/WPF runtime, rendered Studio, and saved only portable data.");
    }
    private static string NewFolder(string artifacts) { var path = Path.Combine(artifacts, "portable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void CopyFolder(string source, string destination) { foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true); foreach (var folder in Directory.GetDirectories(source)) { var target = Path.Combine(destination, Path.GetFileName(folder)); Directory.CreateDirectory(target); CopyFolder(folder, target); } }
    private static void Check(bool value) { if (!value) throw new Exception("Portable storage assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
