using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed class ActivationModule : IDevToolsModule
{
    private readonly Func<IModuleContext, IWindowsActivationBackend> create;
    public ActivationModule() : this(c => new WindowsActivationBackend(c)) { }
    public ActivationModule(Func<IModuleContext, IWindowsActivationBackend> create) => this.create = create;
    public string Id => "activation";
    public static readonly IReadOnlyDictionary<string, string> Keys = ActivationCatalog.Keys;
    public ModulePage CreatePage(IModuleContext c)
    {
        var workspace = new WindowsWorkspace { Kind = "Activation" };
        var p = new ModulePage(Id, "Windows activation", "Your Windows edition, license status and matching public key.", "Windows", "\uE73E") { WindowsWorkspace = workspace };
        p.Add("detected", "Detected Windows edition", "", FieldKind.ReadOnly); p.Add("edition", "Edition ID", "", FieldKind.ReadOnly);
        p.Add("custom", "Use my own licensed product key", "false", FieldKind.Toggle); p.Add("key", "Your product key", "", FieldKind.Secret);
        p.Field(c, "server", "Organization KMS server", hint: "Leave blank for DNS auto-discovery. Public volume keys do not provide a license.");
        var backend = create(c);
        void UpdateEnabled() { if (workspace.ActivateAction is { } action) action.IsEnabled = p["custom"].IsChecked ? System.Text.RegularExpressions.Regex.IsMatch(p["key"].Value, "^[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4}$") : workspace.PublicKey != ""; }
        void Detect()
        {
            var edition = backend.ReadEdition(); workspace.Edition = edition.Display; workspace.Heading = edition.Display;
            workspace.Description = "Edition detected automatically. Suggested key selection does not change Windows.";
            p["detected"].Value = edition.Display; p["edition"].Value = edition.Id;
            workspace.PublicKey = ActivationCatalog.Match(edition) ?? "";
            workspace.KeyNote = workspace.PublicKey == "" ? "No matching public volume key for this edition. Use your own licensed key or Windows activation settings." : "Selected automatically for " + edition.Display + ". Applying it requires confirmation.";
            UpdateEnabled();
        }
        async Task Refresh(CancellationToken ct)
        {
            Detect(); workspace.License = "Checking…";
            try { var license = await backend.ReadLicenseAsync(ct); workspace.License = license.Status; workspace.Badge = license.Status; workspace.IsPositive = license.Activated; workspace.IsInitialized = true; }
            catch { workspace.License = workspace.Badge = "Status unavailable"; workspace.IsPositive = false; throw; }
        }
        p.Action("Refresh activation", Refresh, c); workspace.RefreshAction = p.Actions[^1];
        p.Action("Check license details", async ct => await c.ShowReportAsync("Windows license details", await backend.DetailsAsync(ct)), c); workspace.DetailsAction = p.Actions[^1];
        p.Action("Install key & activate", async ct => {
            var custom = p["custom"].IsChecked; var key = custom ? p["key"].Value.Trim().ToUpperInvariant() : workspace.PublicKey;
            var server = p["server"].Value.Trim(); WindowsActivationBackend.ValidateInput(key, server);
            if (!await c.ConfirmAsync("Confirm Windows activation", $"Install the selected {(custom ? "custom licensed" : "Microsoft public volume")} key for {workspace.Edition} and request activation? This can replace your currently installed key. Public volume keys require your organization's licensed activation infrastructure.")) return;
            try { var result = await backend.ActivateAsync(key, server, !custom, ct); c.Save("activation.server", server); await Refresh(ct); await c.ShowReportAsync("Windows activation", result); }
            finally { p["key"].Value = ""; }
        }, c, true); workspace.ActivateAction = p.Actions[^1];
        p.Action("Windows activation settings", ct => { c.Open("ms-settings:activation"); return Task.CompletedTask; }, c); workspace.SettingsAction = p.Actions[^1];
        foreach (var name in new[] { "custom", "key" }) p[name].PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModuleField.Value)) UpdateEnabled(); };
        workspace.Badge = "Not checked"; Detect(); return p;
    }
}
