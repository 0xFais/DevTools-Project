using DevTools.Contracts;
using DevTools.Infrastructure;
using System.Text.Json;

namespace DevTools.Modules;

public sealed class SettingsModule : IDevToolsModule
{
    public string Id => "settings";
    public ModulePage CreatePage(IModuleContext c)
    {
        var p = new ModulePage(Id, "Settings", "App preferences, configuration import and trusted extensions.", "Application", "\uE713");
        p.Field(c, "theme", "Appearance", "Dark", FieldKind.Choice, choices: ["Dark", "Light", "System"]);
        var settingsDirectory = (c as IAppDataLocations)?.SettingsDirectory ?? c.DataDirectory;
        var logsDirectory = (c as IAppDataLocations)?.LogsDirectory ?? c.DataDirectory;
        p.Add("data", "Settings folder", settingsDirectory, FieldKind.ReadOnly);
        p.Add("logs", "Logs folder", logsDirectory, FieldKind.ReadOnly);
        p.Add("portable", "Portable app", "The small ZIP requires .NET 10 Desktop Runtime. Preferences stay in Data beside DevTools.exe; Windows backups and logs are separated by PC/user. Keep the folder accessible and .NET installed while timed update recovery is pending.", FieldKind.ReadOnly);
        p.Add("security", "Extension access", "Enabled extensions execute with the same administrator access as DevTools. Enable only extensions you trust; restart after changes.", FieldKind.ReadOnly);
        var extensionRoot = Path.Combine(AppContext.BaseDirectory, "Extensions");
        Directory.CreateDirectory(extensionRoot);
        foreach (var manifestPath in Directory.EnumerateFiles(extensionRoot, "extension.json", SearchOption.AllDirectories))
        {
            try
            {
                var m = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (m is not null)
                    p.Add("extension." + m.Id, "Enable extension · " + m.Name, c.Get("extensions.enabled." + m.Id, "false"), FieldKind.Toggle);
            }
            catch (JsonException) { c.Log("Extensions", "Invalid manifest: " + manifestPath); }
        }
        p.Action("Save preferences", ct => { ModuleTools.SaveFields(p, c); foreach (var f in p.Fields.Where(f => f.Key.StartsWith("extension."))) c.Save("extensions.enabled." + f.Key[10..], f.Value); return Task.CompletedTask; }, c, true);
        p.Action("Open extensions folder", ct => { c.Open(extensionRoot); return Task.CompletedTask; }, c);
        p.Action("Open settings folder", ct => { c.Open(settingsDirectory); return Task.CompletedTask; }, c);
        p.Action("Open logs folder", ct => { Directory.CreateDirectory(logsDirectory); c.Open(logsDirectory); return Task.CompletedTask; }, c);
        p.Action("Import previous DevTools settings", async ct =>
        {
            if (await c.PickFileAsync("Import previous DevTools INI", "DevTools settings|devtools.ini|INI files|*.ini") is not { } file)
                return;
            var ini = SettingsStore.ReadIni(file);
            if (!await c.ConfirmAsync("Import previous settings", "Import saved application paths? No system settings will be applied. Reopen DevTools to use imported values."))
                return;
            string Read(string section, string key) => ini.TryGetValue(section, out var s) ? s.GetValueOrDefault(key, "") : "";
            foreach (var (section, key, target) in new[] { ("paths", "pgctl", "postgresql.exe"), ("paths", "pgdata", "postgresql.data"), ("paths", "pgsvc", "postgresql.service") })
            {
                var value = Read(section, key);
                if (value != "")
                    c.Save(target, target.EndsWith(".host") ? value.Replace("Host", "") : value);
            }
            if (ini.TryGetValue("context_paths", out var paths))
                foreach (var pair in paths)
                    c.Save("context.path." + pair.Key, pair.Value);
            await c.ShowReportAsync("Import complete", "Saved paths imported. Restart to refresh all modules. Custom application definitions and protocol ownership backups are kept separately; recreate those in this application.");
        }, c);
        return p;
    }
}
