using System.Text.Json;
using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed class UserFoldersModule(IFolderLocationBackend? backend = null) : IDevToolsModule
{
    public string Id => "user-folders";
    public ModulePage CreatePage(IModuleContext context)
    {
        var workspace = new FolderWorkspace();
        var page = new ModulePage(Id, "User folders", "Manage Windows folder locations and your own folders.", "Windows", "\uE8B7") { FolderWorkspace = workspace };
        _ = new FolderInspectorController(workspace, page, context, backend ?? new WindowsFolderLocationBackend());
        return page;
    }
}
public sealed record CustomFolder(string Id, string Name, string Path);
public sealed class FolderInspectorController
{
    private readonly FolderWorkspace w;
    private readonly IModuleContext c;
    private readonly FolderLocationService service;
    private List<FolderItem> all = [];
    private string Store => Path.Combine(c.DataDirectory, "custom-folders.json");
    private List<CustomFolder> Customs() => File.Exists(Store) ? JsonSerializer.Deserialize<List<CustomFolder>>(File.ReadAllText(Store)) ?? [] : [];
    private void Save(List<CustomFolder> items) { Directory.CreateDirectory(c.DataDirectory); File.WriteAllText(Store + ".tmp", JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })); File.Move(Store + ".tmp", Store, true); }
    public FolderInspectorController(FolderWorkspace workspace, ModulePage page, IModuleContext context, IFolderLocationBackend backend)
    {
        (w, c, service) = (workspace, context, new FolderLocationService(backend));
        void Add(string name, string title, Func<CancellationToken, Task> action, bool primary = false) { var command = new ModuleAction(title, page, action, c, primary); w.Commands[name] = command; page.Actions.Add(command); }
        w.SearchChanged += Filter;
        w.SelectionChanged += () => { if (w.Selected is { } selected) { w.IsCustomEditor = false; w.Target = selected.CurrentPath; w.MoveFiles = false; w.Notice = selected.CloudManaged ? "Managed by OneDrive. Stop its folder backup in OneDrive before changing the location here." : selected.IsCustom ? "Custom folders move their contents; Windows folder settings stay unchanged." : "Choose a new location. Existing files stay in place unless you select Move existing files."; } };
        Add("Refresh", "Refresh folders", Refresh);
        Add("AddCustom", "Add custom folder", ct => { w.Selected = null; w.IsCustomEditor = true; w.CustomName = ""; w.CustomSource = ""; w.Notice = "Save a named folder here, then select it to move it or open it."; return Task.CompletedTask; });
        Add("BrowseSource", "Choose custom folder", async ct => { if (await c.PickFolderAsync("Select your custom folder") is { } source) { w.CustomSource = source; if (w.CustomName.Length == 0) w.CustomName = Path.GetFileName(source); } });
        Add("SaveCustom", "Save custom folder", async ct =>
        {
            var name = w.CustomName.Trim(); var source = Path.GetFullPath(w.CustomSource);
            if (name.Length == 0 || name.Length > 100) throw new ArgumentException("Enter a folder name of up to 100 characters.");
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Choose an existing folder.");
            var items = Customs(); if (items.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || FolderLocationService.Same(f.Path, source))) throw new ArgumentException("That name or folder is already saved.");
            if (!await c.ConfirmAsync("Save custom folder", $"Save {name}\n{source}\n\nNo files will be moved.")) throw new ActionDeclinedException();
            var custom = new CustomFolder(Guid.NewGuid().ToString("N"), name, source); items.Add(custom); Save(items); w.IsCustomEditor = false; w.Search = ""; await Refresh(ct); w.Selected = w.Items.FirstOrDefault(f => f.Id == custom.Id);
        });
        Add("BrowseTarget", "Choose destination", async ct => { if (await c.PickFolderAsync("Select destination, or type a new folder path in the inspector") is { } target) w.Target = target; });
        Add("Review", "Review folder change", ct => Change(w.Target, ct), true);
        Add("Default", "Restore default location", ct => { var item = Selected(); if (item.IsCustom) throw new InvalidOperationException("Custom folders have no Windows default location."); if (FolderLocationService.Same(item.CurrentPath, item.DefaultPath)) { w.Notice = "This folder is already at its default location."; return Task.CompletedTask; } w.Target = item.DefaultPath; return Change(item.DefaultPath, ct); });
        Add("Open", "Open folder", ct => { c.Open(Selected().CurrentPath); return Task.CompletedTask; });
        Add("RemoveCustom", "Remove saved folder", async ct => { var item = Selected(); if (!item.IsCustom) throw new InvalidOperationException("Windows folders cannot be removed from this list."); if (!await c.ConfirmAsync("Remove saved folder", "Remove " + item.Name + " from this list? Its files and folder will be kept.")) throw new ActionDeclinedException(); Save(Customs().Where(f => f.Id != item.Id).ToList()); await Refresh(ct); });
        Add("Journals", "Open change journals", ct => { var directory = Path.Combine(c.DataDirectory, "FolderChanges"); Directory.CreateDirectory(directory); c.Open(directory); return Task.CompletedTask; });
        // Initial inspection is read-only and keeps all known folders available immediately.
        Reload(); Filter();
    }
    private FolderItem Selected() => w.Selected ?? throw new InvalidOperationException("Select a folder first.");
    private void Reload() { all = service.Read(); all.AddRange(Customs().Select(f => new FolderItem(f.Id, f.Name, f.Path, "", "\uE8B7", true))); w.Summary = $"{all.Count(f => !f.IsCustom)} Windows folders · {all.Count(f => f.IsCustom)} custom folders"; }
    private Task Refresh(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Reload(); Filter(); return Task.CompletedTask; }
    private void Filter()
    {
        var id = w.Selected?.Id;
        w.Items.Clear(); foreach (var item in all.Where(f => $"{f.Name} {f.CurrentPath} {f.Kind} {f.Status}".Contains(w.Search.Trim(), StringComparison.OrdinalIgnoreCase))) w.Items.Add(item);
        w.Selected = w.Items.FirstOrDefault(f => f.Id == id) ?? (w.IsCustomEditor ? null : w.Items.FirstOrDefault());
        if (w.Selected is null && !w.IsCustomEditor) w.Notice = "No matching folders. Clear the search to show all folders.";
    }
    private async Task Change(string requestedTarget, CancellationToken ct)
    {
        var item = Selected(); var move = item.IsCustom || w.MoveFiles;
        var target = FolderLocationService.Validate(item, requestedTarget, all, move);
        if (!await c.ConfirmAsync("Review folder change", $"{item.Name} · {item.Kind}\n\nFrom: {item.CurrentPath}\nTo: {target}\n\n" + (move ? "Copy and verify the contents, update the location, then remove verified original files. Original directories will be kept." : "Change the Windows location only. Existing files will stay where they are.") + "\n\nA change journal will be saved. Continue?")) throw new ActionDeclinedException();
        var progress = new Progress<string>(line => c.Log("User folders", line));
        try
        {
            await service.Change(item, target, move, Path.Combine(c.DataDirectory, "FolderChanges"), updated => { var items = Customs(); var index = items.FindIndex(f => f.Id == item.Id); if (index < 0) throw new IOException("The saved custom folder changed externally."); items[index] = items[index] with { Path = updated }; Save(items); }, ct, progress);
        }
        finally { Reload(); Filter(); }
        w.Notice = item.IsCustom ? "Custom folder moved. No Windows folder settings were changed." : "Windows confirmed the new location. Reopen applications; some may need sign-out to refresh their cached paths.";
    }
}
