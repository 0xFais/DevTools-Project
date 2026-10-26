using DevTools.Contracts;
using DevTools.Infrastructure;

namespace DevTools.Modules.Integrations;

public sealed class ContextMenuRegistrations(IIntegrationRegistry registry)
{
    public IntegrationItem Inspect(AppProfile app, string savedPath)
    {
        var locations = new List<string>();
        var command = "";
        var machine = false;
        foreach (var scope in ContextMenuModule.Scopes)
        {
            var key = scope.Value + ContextMenuModule.RegistryKeyName(app) + @"\command";
            var userCommand = registry.Read(IntegrationHive.User, key);
            var value = userCommand ?? registry.Read(IntegrationHive.Machine, key);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            locations.Add(scope.Key);
            command = command == "" ? value : command;
            machine |= userCommand is null;
        }
        var path = IntegrationCommands.Executable(command);
        if (path == "")
            path = savedPath;
        return new(app.Id, app.Name, path, locations.Count > 0, File.Exists(path), string.Join(" · ", locations), !machine, app.Id.StartsWith("custom_"), machine ? "This registration is managed for all users. Its details are shown read-only." : "Changes apply only to this application's per-user Explorer entries.");
    }
    public IEnumerable<IntegrationItem> OtherRegistrations(IEnumerable<AppProfile> catalog)
    {
        var known = catalog.Select(ContextMenuModule.RegistryKeyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<string, IntegrationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { IntegrationHive.User, IntegrationHive.Machine })
            foreach (var scope in ContextMenuModule.Scopes)
                foreach (var key in registry.SubKeys(hive, scope.Value.TrimEnd('\\')))
                {
                    if (known.Contains(key))
                        continue;
                    var path = scope.Value + key;
                    if (hive == IntegrationHive.Machine && registry.Read(IntegrationHive.User, path + @"\command") is not null)
                        continue;
                    var executable = IntegrationCommands.Executable(registry.Read(hive, path + @"\command") ?? "");
                    if (executable == "")
                        continue;
                    var label = registry.Read(hive, path, "MUIVerb") ?? registry.Read(hive, path) ?? key;
                    if (label.StartsWith('@') || label.Length == 0)
                        label = key;
                    var id = "external:" + key + ":" + executable.ToLowerInvariant();
                    if (rows.TryGetValue(id, out var previous))
                        rows[id] = previous with
                        {
                            Locations = previous.Locations + " · " + scope.Key
                        };
                    else
                        rows[id] = new(id, label.Replace("&", ""), executable, true, File.Exists(executable), scope.Key, false, Notice: "Detected outside DevTools. Manage this registration in its owning application; Add custom can create a separate DevTools profile.");
                }
        return rows.Values;
    }
    public void Save(AppProfile app, string executable, IEnumerable<string> scopes)
    {
        IntegrationCommands.ValidateExecutable(executable);
        var selected = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Any(s => !ContextMenuModule.Scopes.ContainsKey(s)))
            throw new ArgumentException("Choose at least one Explorer location.");
        foreach (var scope in ContextMenuModule.Scopes)
        {
            var path = scope.Value + ContextMenuModule.RegistryKeyName(app);
            if (!selected.Contains(scope.Key))
            {
                registry.DeleteTree(path);
                continue;
            }
            registry.Write(path, "", app.Mode == "terminal" ? "Open " + app.Name + " here" : "Open with " + app.Name);
            registry.Write(path, "Icon", '"' + executable + '"');
            registry.Write(path + @"\command", "", WindowsEnvironment.ContextCommand(app.Id, executable, scope.Key));
        }
    }
    public void Remove(AppProfile app)
    {
        foreach (var scope in ContextMenuModule.Scopes.Values)
            registry.DeleteTree(scope + ContextMenuModule.RegistryKeyName(app));
    }
}
