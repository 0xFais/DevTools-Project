using DevTools.Contracts;
using DevTools.Infrastructure;
using System.Text.Json;

namespace DevTools.Modules.Integrations;

public record AppProfile(string Id, string Name, string Exe, string Mode, string Key);
public sealed class ContextMenuModule : IDevToolsModule
{
    private readonly IIntegrationRegistry registry;
    private readonly IInstalledAppDiscovery discovery;
    public ContextMenuModule() : this(new IntegrationRegistry(), new InstalledAppDiscovery()) { }
    public ContextMenuModule(IIntegrationRegistry registry, IInstalledAppDiscovery discovery) => (this.registry, this.discovery) = (registry, discovery);
    public string Id => "context-menu";
    public static string RegistryKeyName(AppProfile app) => app.Id is "code" or "terminal" or "rider" ? app.Key : "DevTools_" + app.Key;
    public static readonly Dictionary<string, string> Scopes = new() { ["files"] = @"Software\Classes\*\shell\", ["folders"] = @"Software\Classes\Directory\shell\", ["background"] = @"Software\Classes\Directory\Background\shell\" };
    public ModulePage CreatePage(IModuleContext c)
    {
        var browser = new IntegrationBrowser();
        var p = new ModulePage(Id, "Context menu", "Manage applications in your Explorer right-click menu.", "Integrations", "\uE8A7") { Browser = browser };
        var apps = JsonSerializer.Deserialize<List<AppProfile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalogs", "context.json")))!;
        var customPath = Path.Combine(c.DataDirectory, "context-custom.json");
        if (File.Exists(customPath))
            apps.AddRange(JsonSerializer.Deserialize<List<AppProfile>>(File.ReadAllText(customPath)) ?? []);
        browser.CatalogNames.AddRange(apps.Select(a => a.Name));
        var registrations = new ContextMenuRegistrations(registry);
        var explicitProfiles = new HashSet<string>();
        foreach (var field in new[] { p.Add("name", "Custom application name"), p.Add("path", "Executable path"), p.Add("mode", "Application type", "Editor", FieldKind.Choice, choices: ["Editor", "Terminal"]), p.Add("files", "Files", "true", FieldKind.Toggle), p.Add("folders", "Folders", "true", FieldKind.Toggle), p.Add("background", "Folder background", "true", FieldKind.Toggle) })
            browser.InspectorFields.Add(field);
        p["name"].IsVisible = p["mode"].IsVisible = false;
        browser.ExtraFields.Add(p.Add("classic", "Classic Explorer menu (Windows 11)", registry.Exists(IntegrationHive.User, ClassicKey) ? "true" : "false", FieldKind.Toggle));
        AppProfile SelectedProfile() => apps.FirstOrDefault(a => a.Id == browser.Selected?.Id) ?? throw new InvalidOperationException("Select an editable app profile first.");
        void PersistCustom() => File.WriteAllText(customPath, JsonSerializer.Serialize(apps.Where(a => a.Id.StartsWith("custom_"))));
        void Refresh(string? preferredId = null)
        {
            var rows = apps.Select(a => registrations.Inspect(a, c.Get("context.path." + a.Id))).Where(item => item.IsAdded || (browser.HasScanned && item.ExecutableExists) || explicitProfiles.Contains(item.Id)).Concat(registrations.OtherRegistrations(apps));
            browser.ReplaceItems(rows, preferredId);
        }
        ModuleAction? save = null;
        void Select()
        {
            var item = browser.Selected;
            if (item is null)
                return;
            var app = apps.FirstOrDefault(a => a.Id == item.Id);
            p["path"].Value = item.ExecutablePath;
            p["name"].Value = item.Name;
            p["name"].IsVisible = false;
            p["mode"].IsVisible = item.IsCustom;
            p["mode"].Value = app?.Mode == "terminal" ? "Terminal" : "Editor";
            foreach (var scope in Scopes.Keys)
                p[scope].IsChecked = item.IsAdded ? item.Locations.Split(" · ").Contains(scope) : scope != "files" || app?.Mode != "terminal";
            browser.Notice = item.Notice;
            if (save is not null)
                save.Title = item.IsAdded ? "Save changes" : "Add to menu";
            foreach (var action in browser.InspectorActions)
                action.IsVisible = action.Title switch
                {
                    "Choose executable" => false,
                    "Remove from menu" => item.IsAdded && item.CanEdit,
                    "Delete custom profile" => item.IsCustom,
                    _ => true
                };
        }
        browser.SelectionChanged += Select;
        void Action(string title, Func<CancellationToken, Task> run, System.Collections.ObjectModel.ObservableCollection<ModuleAction> group, bool primary = false)
        {
            p.Action(title, run, c, primary);
            group.Add(p.Actions[^1]);
        }
        Action("Scan installed apps", async ct =>
        {
            p.Status = "Scanning supported apps and portable folders…";
            var found = await Task.Run(() => discovery.Find(apps.Select(a => a.Exe), DevTools.Modules.Scoop.ScoopService.DetectRoot(c.Get("scoop.root")), ct), ct);
            foreach (var app in apps)
                if (found.TryGetValue(app.Exe, out var path))
                    c.Save("context.path." + app.Id, path);
            browser.HasScanned = true;
            Refresh();
            c.Log(p.Title, browser.Summary + ". Scan did not change Explorer registrations.");
        }, browser.ToolbarActions, true);
        Action("Refresh added", ct => { Refresh(); return Task.CompletedTask; }, browser.ToolbarActions);
        Action("Add custom", ct =>
        {
            browser.Selected = null;
            browser.IsCustomEditor = true;
            p["name"].Value = p["path"].Value = "";
            p["name"].IsVisible = p["mode"].IsVisible = true;
            p["mode"].Value = "Editor";
            foreach (var scope in Scopes.Keys)
                p[scope].IsChecked = true;
            browser.Notice = "Create a profile first. Nothing is added to Explorer until you choose Add to menu.";
            if (save is not null)
                save.Title = "Create custom profile";
            foreach (var action in browser.InspectorActions)
                action.IsVisible = action == save;
            return Task.CompletedTask;
        }, browser.ToolbarActions);
        Action("Save changes", async ct =>
        {
            if (browser.IsCustomEditor)
            {
                var name = ModuleTools.Need(p, "name");
                var exe = ModuleTools.Need(p, "path");
                IntegrationCommands.ValidateExecutable(exe);
                if (apps.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("That application name already exists.");
                var id = "custom_" + Guid.NewGuid().ToString("N");
                apps.Add(new(id, name, Path.GetFileName(exe), p["mode"].Value == "Terminal" ? "terminal" : "editor", id));
                c.Save("context.path." + id, exe);
                PersistCustom();
                browser.CatalogNames.Add(name);
                explicitProfiles.Add(id);
                browser.IsCustomEditor = false;
                Refresh(id);
                return;
            }
            var app = SelectedProfile();
            if (browser.Selected?.CanEdit != true)
                throw new InvalidOperationException("This registration is read-only.");
            var path = ModuleTools.Need(p, "path");
            IntegrationCommands.ValidateExecutable(path);
            var scopes = Scopes.Keys.Where(s => p[s].IsChecked).ToArray();
            if (scopes.Length == 0)
                throw new ArgumentException("Choose at least one location.");
            if (!await c.ConfirmAsync("Update Explorer integration", $"Save {app.Name} for {string.Join(", ", scopes)}? Unchecked per-user locations for this profile will be removed."))
                return;
            if (app.Id.StartsWith("custom_"))
            {
                var updated = app with
                {
                    Mode = p["mode"].Value == "Terminal" ? "terminal" : "editor"
                };
                apps[apps.IndexOf(app)] = updated;
                app = updated;
                PersistCustom();
            }
            registrations.Save(app, path, scopes);
            c.Save("context.path." + app.Id, path);
            Refresh(app.Id);
        }, browser.InspectorActions, true);
        save = p.Actions[^1];
        Action("Choose executable", async ct => { if (await c.PickFileAsync("Application executable", "Applications|*.exe") is { } file) p["path"].Value = file; }, browser.InspectorActions);
        browser.BrowseAction = p.Actions[^1];
        browser.BrowseAction.IsVisible = false;
        Action("Remove from menu", async ct => { var app = SelectedProfile(); if (browser.Selected?.CanEdit != true) throw new InvalidOperationException("This registration is read-only."); if (!await c.ConfirmAsync("Remove context entry", "Remove the per-user Explorer entries for " + app.Name + "?")) return; registrations.Remove(app); explicitProfiles.Remove(app.Id); Refresh(app.Id); }, browser.InspectorActions);
        Action("Delete custom profile", async ct => { var app = SelectedProfile(); if (!app.Id.StartsWith("custom_")) throw new InvalidOperationException("Only custom profiles can be deleted."); if (!await c.ConfirmAsync("Delete custom profile", "Remove this profile and its per-user Explorer entries?")) return; registrations.Remove(app); apps.Remove(app); explicitProfiles.Remove(app.Id); PersistCustom(); Refresh(); }, browser.InspectorActions);
        Action("Apply classic menu", async ct => { if (!await c.ConfirmAsync("Explorer menu style", "Apply the preference? Restart Explorer manually for it to take effect.")) return; if (p["classic"].IsChecked) registry.Write(ClassicKey + @"\InprocServer32", "", ""); else registry.DeleteTree(ClassicKey); }, browser.ExtraActions);
        Refresh();
        p.Status = "Existing Explorer registrations loaded automatically.";
        return p;
    }
    private const string ClassicKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
}
