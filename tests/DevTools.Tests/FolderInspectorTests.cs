using System.IO;
using System.Windows.Threading;
using DevTools.Contracts;
using DevTools.Modules.Windows;

namespace DevTools.Tests;

internal static class FolderInspectorTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Folder inspector assertion failed."); }
    private static void Reject(Action action) { var rejected = false; try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { rejected = true; } Check(rejected); }
    private static IProgress<string> Log(Action<string>? callback = null) => new InlineProgress(callback ?? (_ => { }));
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Folder inspector discovers six canonical Windows folders and their defaults", () =>
        {
            var f = new Fixture(artifacts); var items = f.Service.Read();
            Check(items.Select(i => i.Id).SequenceEqual(new[] { "desktop", "documents", "downloads", "pictures", "music", "videos" }));
            Check(FolderLocationService.Definitions.Single(d => d.Id == "downloads").Guid == new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            Check(items.All(i => i.Status == "Default location") && f.Backend.Writes == 0);
        });
        run("Changing only a Windows location keeps existing files untouched", () =>
        {
            var f = new Fixture(artifacts); f.Change(false); Check(File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")) && !File.Exists(Path.Combine(f.Target, "file.txt")) && f.Backend.Paths[f.Item.Id] == f.Target);
        });
        run("File move verifies copies, preserves empty directories and removes original files only", () =>
        {
            var f = new Fixture(artifacts); f.Change(true);
            Check(File.ReadAllText(Path.Combine(f.Target, "file.txt")) == "original data" && !File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")));
            Check(Directory.Exists(f.Item.CurrentPath) && Directory.Exists(Path.Combine(f.Target, "empty")) && File.ReadAllText(Directory.GetFiles(f.Journals).Single()).Contains("Completed"));
        });
        run("Custom folder moves update only the saved path, not Windows settings", () =>
        {
            var f = new Fixture(artifacts); var path = ""; var source = Path.Combine(f.Root, "projects"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "file.txt"), "custom data"); var custom = f.Item with { Id = "custom", IsCustom = true, CurrentPath = source };
            f.Service.Change(custom, f.Target, true, f.Journals, updated => path = updated, CancellationToken.None, Log()).GetAwaiter().GetResult();
            Check(path == f.Target && f.Backend.Writes == 0 && File.Exists(Path.Combine(f.Target, "file.txt")));
        });
        run("Destination validation rejects overlap, drive roots and occupied move targets", () =>
        {
            var f = new Fixture(artifacts);
            foreach (var target in new[] { f.Item.CurrentPath, Path.Combine(f.Item.CurrentPath, "child"), Path.GetDirectoryName(f.Item.CurrentPath)!, Path.GetPathRoot(f.Target)!, "relative" }) Reject(() => FolderLocationService.Validate(f.Item, target, [], true));
            Directory.CreateDirectory(f.Target); File.WriteAllText(Path.Combine(f.Target, "keep.txt"), "keep"); Reject(() => FolderLocationService.Validate(f.Item, f.Target, [], true)); Check(File.ReadAllText(Path.Combine(f.Target, "keep.txt")) == "keep");
        });
        run("Folder changes cannot overlap another Windows folder or bypass it using a custom entry", () =>
        {
            var f = new Fixture(artifacts); var other = f.Service.Read().First(i => i.Id != f.Item.Id);
            Reject(() => FolderLocationService.Validate(f.Item, other.CurrentPath, [other], false));
            Reject(() => FolderLocationService.Validate(f.Item with { Id = "custom", IsCustom = true }, f.Target, [f.Item], true));
        });
        run("OneDrive-managed folder changes are blocked before mutation", () =>
        {
            var f = new Fixture(artifacts); Reject(() => FolderLocationService.Validate(f.Item with { CloudManaged = true }, f.Target, [], false)); Check(f.Backend.Writes == 0);
        });
        run("Failed Windows redirection retains original and copied data", () =>
        {
            var f = new Fixture(artifacts); f.Backend.Fail = true; Reject(() => f.Change(true));
            Check(File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")) && File.Exists(Path.Combine(f.Target, "file.txt")) && f.Backend.Paths[f.Item.Id] == f.Item.CurrentPath);
        });
        run("Cancellation during copying keeps all original files and writes a recovery journal", () =>
        {
            var f = new Fixture(artifacts); File.WriteAllText(Path.Combine(f.Item.CurrentPath, "second.txt"), "second"); using var cancel = new CancellationTokenSource(); var cancelled = false;
            try { f.Service.Change(f.Item, f.Target, true, f.Journals, _ => { }, cancel.Token, Log(_ => cancel.Cancel())).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")) && File.Exists(Path.Combine(f.Item.CurrentPath, "second.txt")) && f.Backend.Writes == 0 && Directory.GetFiles(f.Journals).Length == 1);
        });
        run("Concurrent source additions stop relocation before Windows is changed", () =>
        {
            var f = new Fixture(artifacts); Reject(() => f.Service.Change(f.Item, f.Target, true, f.Journals, _ => { }, CancellationToken.None, Log(_ => File.WriteAllText(Path.Combine(f.Item.CurrentPath, "new.txt"), "new"))).GetAwaiter().GetResult());
            Check(f.Backend.Writes == 0 && File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")));
        });
        run("Search, custom folder persistence and removal keep files intact", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.Combine(f.Root, "data")); var page = new UserFoldersModule(f.Backend).CreatePage(context); var w = page.FolderWorkspace!;
            w.Search = "downloads"; Check(w.Items.Single().Id == "downloads"); w.Search = "";
            Act(w, context, "AddCustom"); w.CustomName = "Projects"; w.CustomSource = f.Item.CurrentPath; Act(w, context, "SaveCustom");
            Check(w.Items.Any(i => i.Name == "Projects" && i.IsCustom)); Act(w, context, "RemoveCustom"); Check(!w.Items.Any(i => i.IsCustom) && File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")));
        });
        run("Declining the folder review makes no file or Windows changes", () =>
        {
            var f = new Fixture(artifacts); var context = new ScoopTests.TestContext(Path.Combine(f.Root, "data")) { Confirm = false }; var w = new UserFoldersModule(f.Backend).CreatePage(context).FolderWorkspace!;
            w.Selected = w.Items.Single(i => i.Id == f.Item.Id); w.Target = f.Target; w.MoveFiles = true; Act(w, context, "Review");
            Check(!Directory.Exists(f.Target) && f.Backend.Writes == 0 && File.Exists(Path.Combine(f.Item.CurrentPath, "file.txt")));
        });
    }
    internal static ModulePage Preview(string artifacts)
    {
        var f = new Fixture(artifacts); var page = new UserFoldersModule(f.Backend).CreatePage(new ScoopTests.TestContext(Path.Combine(f.Root, "data")));
        page.FolderWorkspace!.Selected = page.FolderWorkspace.Items.Single(i => i.Id == "downloads"); return page;
    }
    private static void Act(FolderWorkspace w, ScoopTests.TestContext context, string key)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { w.Commands[key].Execute(null); var end = DateTime.UtcNow.AddSeconds(15); while (context.IsOperationRunning && DateTime.UtcNow < end) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } Check(!context.IsOperationRunning); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string line) => action(line); }
    private sealed class Fixture
    {
        internal readonly string Root, Target, Journals;
        internal readonly FakeBackend Backend = new();
        internal readonly FolderLocationService Service;
        internal FolderItem Item { get; }
        internal Fixture(string artifacts)
        {
            Root = Path.Combine(artifacts, "folder-" + Guid.NewGuid().ToString("N")); Target = Path.Combine(Root, "target"); Journals = Path.Combine(Root, "journals");
            foreach (var definition in FolderLocationService.Definitions) { var path = Path.Combine(Root, "source-" + definition.Name); Directory.CreateDirectory(path); Backend.Paths[definition.Id] = path; Backend.Defaults[definition.Id] = path; }
            Service = new(Backend); Item = Service.Read().Single(i => i.Id == "downloads"); File.WriteAllText(Path.Combine(Item.CurrentPath, "file.txt"), "original data"); Directory.CreateDirectory(Path.Combine(Item.CurrentPath, "empty"));
        }
        internal void Change(bool move) { var item = Item; Service.Change(item, Target, move, Journals, _ => { }, CancellationToken.None, Log()).GetAwaiter().GetResult(); }
    }
    private sealed class FakeBackend : IFolderLocationBackend
    {
        internal readonly Dictionary<string, string> Paths = [], Defaults = [];
        internal bool Fail;
        internal int Writes;
        public string Read(KnownFolderDefinition folder, bool defaultPath = false) => (defaultPath ? Defaults : Paths)[folder.Id];
        public void Set(KnownFolderDefinition folder, string path) { if (Fail) throw new IOException("fixture redirect failure"); Paths[folder.Id] = path; Writes++; }
    }
}
