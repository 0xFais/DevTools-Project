using DevTools.Contracts;
using DevTools.Infrastructure;
using System.Text.Json;

namespace DevTools.Modules.Integrations;

public record LinkProfile(string Id, string Name, string Exe, string Scheme);
public sealed class LinkHandlersModule : IDevToolsModule
{
    private readonly IIntegrationRegistry registry;
    private readonly IInstalledAppDiscovery discovery;
    public LinkHandlersModule() : this(new IntegrationRegistry(), new InstalledAppDiscovery()) { }
    public LinkHandlersModule(IIntegrationRegistry registry, IInstalledAppDiscovery discovery) => (this.registry, this.discovery) = (registry, discovery);
    public string Id => "link-handlers";
    public ModulePage CreatePage(IModuleContext c)
    {
        var browser = new IntegrationBrowser { SearchIncludesLocations = true };
        var p = new ModulePage(Id, "Link handlers", "Manage applications that open URL protocols.", "Integrations", "\uE71B") { Browser = browser };
        var apps = JsonSerializer.Deserialize<List<LinkProfile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalogs", "links.json")))!;
        var customPath = Path.Combine(c.DataDirectory, "link-custom.json");
        if (File.Exists(customPath))
            apps.AddRange(JsonSerializer.Deserialize<List<LinkProfile>>(File.ReadAllText(customPath)) ?? []);
        browser.CatalogNames.AddRange(apps.Select(a => a.Name));
        var registrations = new LinkRegistrations(registry);
        var explicitProfiles = new HashSet<string>();
        foreach (var field in new[] { p.Add("customName", "Custom application name"), p.Add("path", "Executable path"), p.Add("scheme", "URL scheme", hint: "For example: magnet, tg, vscode.") })
            browser.InspectorFields.Add(field);
        p["customName"].IsVisible = false;
        LinkProfile SelectedProfile() => apps.FirstOrDefault(a => a.Id == browser.Selected?.Id) ?? throw new InvalidOperationException("Select an editable app profile first.");
        void PersistCustom() => File.WriteAllText(customPath, JsonSerializer.Serialize(apps.Where(a => a.Id.StartsWith("custom_"))));
        void Refresh(string? preferredId = null)
        {
            var rows = apps.Select(a => registrations.Inspect(a, c.Get("links.path." + a.Id)))
                .Where(item => item.IsAdded || (browser.HasScanned && item.ExecutableExists) || explicitProfiles.Contains(item.Id)).Concat(registrations.OtherRegistrations(apps));
            browser.ReplaceItems(rows, preferredId);
        }
        ModuleAction? save = null;
        browser.SelectionChanged += () =>
        {
            var item = browser.Selected;
            if (item is null)
                return;
            p["customName"].IsVisible = false;
            p["path"].Value = item.ExecutablePath;
            p["scheme"].Value = item.Locations.TrimEnd(':');
            p["scheme"].IsReadOnly = !item.IsCustom;
            browser.Notice = item.Notice;
            if (save is not null)
                save.Title = registrations.UserChoice(p["scheme"].Value) != "" ? "Change Windows default" : "Register handler";
            foreach (var action in browser.InspectorActions)
                action.IsVisible = action.Title switch
                {
                    "Choose executable" => false,
                    "Restore previous handler" => c.Get("links.owned." + p["scheme"].Value) is { Length: > 0 } owned && registrations.UserCommand(p["scheme"].Value) == owned,
                    "Delete custom profile" => item.IsCustom,
                    _ => true
                };
        };
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
                    c.Save("links.path." + app.Id, path);
            browser.HasScanned = true;
            Refresh();
            c.Log(p.Title, browser.Summary + ". Scan did not register any URL handlers.");
        }, browser.ToolbarActions, true);
        Action("Refresh added", ct => { Refresh(); return Task.CompletedTask; }, browser.ToolbarActions);
        Action("Add custom", ct =>
        {
            browser.Selected = null;
            browser.IsCustomEditor = true;
            p["customName"].IsVisible = true;
            p["customName"].Value = p["path"].Value = p["scheme"].Value = "";
            p["scheme"].IsReadOnly = false;
            browser.Notice = "Create a profile first. Registering it is a separate action.";
            if (save is not null)
                save.Title = "Create custom profile";
            foreach (var action in browser.InspectorActions)
                action.IsVisible = action == save;
            return Task.CompletedTask;
        }, browser.ToolbarActions);
        Action("Register handler", async ct =>
        {
            var scheme = ModuleTools.Need(p, "scheme").ToLowerInvariant();
            if (!LinkRegistrations.ValidScheme(scheme))
                throw new ArgumentException("Invalid URL scheme.");
            var exe = ModuleTools.Need(p, "path");
            IntegrationCommands.ValidateExecutable(exe);
            if (browser.IsCustomEditor)
            {
                var name = ModuleTools.Need(p, "customName");
                if (apps.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("That name already exists.");
                var app = new LinkProfile("custom_" + Guid.NewGuid().ToString("N"), name, Path.GetFileName(exe), scheme);
                apps.Add(app);
                PersistCustom();
                c.Save("links.path." + app.Id, exe);
                browser.CatalogNames.Add(name);
                explicitProfiles.Add(app.Id);
                browser.IsCustomEditor = false;
                Refresh(app.Id);
                return;
            }
            var selected = SelectedProfile();
            if (registrations.UserChoice(scheme) != "")
            {
                c.Open("ms-settings:defaultapps");
                return;
            }
            if (!await c.ConfirmAsync("Register URL handler", $"Use {selected.Name} for {scheme}: links? The previous per-user command will be saved for restoration."))
                return;
            registrations.Register(scheme, exe, c);
            c.Save("links.path." + selected.Id, exe);
            if (selected.Id.StartsWith("custom_") && selected.Scheme != scheme)
            {
                apps[apps.IndexOf(selected)] = selected with
                {
                    Scheme = scheme
                };
                PersistCustom();
            }
            Refresh(selected.Id);
        }, browser.InspectorActions, true);
        save = p.Actions[^1];
        Action("Choose executable", async ct => { if (await c.PickFileAsync("Protocol handler", "Applications|*.exe") is { } file) p["path"].Value = file; }, browser.InspectorActions);
        browser.BrowseAction = p.Actions[^1];
        browser.BrowseAction.IsVisible = false;
        Action("Restore previous handler", async ct => { var scheme = ModuleTools.Need(p, "scheme").ToLowerInvariant(); if (!await c.ConfirmAsync("Restore URL handler", "Restore the previous per-user command for " + scheme + "?")) return; registrations.Restore(scheme, c); explicitProfiles.Remove(browser.Selected?.Id ?? ""); Refresh(); }, browser.InspectorActions);
        Action("Delete custom profile", async ct => { var app = SelectedProfile(); if (!app.Id.StartsWith("custom_")) throw new InvalidOperationException("Only custom profiles can be deleted."); if (!await c.ConfirmAsync("Delete custom profile", "Delete the profile? Use Restore previous handler first if you want to undo its registration.")) return; apps.Remove(app); explicitProfiles.Remove(app.Id); PersistCustom(); Refresh(); }, browser.InspectorActions);
        Action("Windows default apps", ct => { c.Open("ms-settings:defaultapps"); return Task.CompletedTask; }, browser.ExtraActions);
        Refresh();
        p.Status = "Existing protocol registrations loaded automatically.";
        return p;
    }
}
