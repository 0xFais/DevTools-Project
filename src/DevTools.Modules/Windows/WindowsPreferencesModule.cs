using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed class WindowsPreferencesModule(string id, IWindowsPreferencesBackend? backend = null) : IDevToolsModule
{
    public string Id => id;
    public static readonly (string Id, string Title, string Description, string Icon, string Settings)[] Pages =
    [
        ("explorer-taskbar", "Explorer & taskbar", "Control how files, menus and the taskbar appear.", "\uE8B7", "ms-settings:taskbar"),
        ("appearance-signin", "Appearance & sign-in", "Choose Windows visuals and startup information.", "\uE790", "ms-settings:personalization-colors"),
        ("privacy-suggestions", "Privacy & suggestions", "Control activity publishing, location and consumer suggestions.", "\uE72E", "ms-settings:privacy"),
        ("power-gaming", "Power & gaming", "Choose hibernation, Game Mode and desktop power settings.", "\uE7E8", "ms-settings:powersleep"),
        ("developer-settings", "Developer settings", "Configure Windows behavior for development tools.", "\uE943", "ms-settings:developers")
    ];
    public ModulePage CreatePage(IModuleContext context)
    {
        var definition = Pages.Single(p => p.Id == id);
        var workspace = new WindowsPreferencesWorkspace();
        var page = new ModulePage(id, definition.Title, definition.Description, "Windows", definition.Icon) { PreferencesWorkspace = workspace };
        var system = backend ?? new WindowsPreferencesBackend();
        var service = new WindowsPreferencesService(system, id, Path.Combine(context.DataDirectory, "WindowsPreferences", id + ".json"));
        foreach (var setting in WindowsPreferenceDefinitions.All.Where(d => d.Page == id))
        {
            var row = new WindowsPreferenceRow { Id = setting.Id, Title = setting.Title, Description = setting.Description, Scope = setting.Scope, Choices = setting.Choices ?? [] };
            row.Changed += workspace.NotifySelection; workspace.Rows.Add(row);
        }
        void Load(PreferencesSnapshot snapshot)
        {
            workspace.Platform = snapshot.Platform; workspace.HasBackup = snapshot.HasBackup;
            foreach (var current in snapshot.Rows)
            {
                var row = workspace.Rows.Single(r => r.Id == current.Id);
                row.Available = current.Available; row.State = current.State; row.Availability = current.Availability; row.Load(current.Enabled);
            }
            workspace.IsInitialized = true; workspace.NotifySelection();
            workspace.RestoreAction!.IsEnabled = workspace.HasBackup;
        }
        async Task Refresh() => Load(await Task.Run(service.Read));
        async Task Finish(PreferencesApplyResult result, bool restore)
        {
            var message = restore ? $"Restored {result.Changed} value(s); preserved {result.Preserved} protected or externally changed value(s)." : $"Saved and read back {result.Changed} value(s).";
            if (result.RestartExplorer)
            {
                workspace.Result = message + " Restarting File Explorer...";
                try { await system.RestartExplorerAsync(CancellationToken.None); message += " File Explorer restarted."; }
                catch { workspace.Result = message + " File Explorer still needs a restart. Use Restart Explorer or sign out and back in."; throw; }
            }
            if (result.RestartWindows) message += " Restart affected apps or Windows to complete the change.";
            workspace.Result = message;
        }
        page.Action("Check current settings", async ct => { ct.ThrowIfCancellationRequested(); await Refresh(); workspace.Result = "Saved settings checked. Select changes, then Apply."; }, context);
        workspace.CheckAction = page.Actions[^1];
        page.Action("Apply changes", async ct =>
        {
            var selected = workspace.Rows.Where(r => r.IsChanged).ToDictionary(r => r.Id, r => r.Enabled);
            var changes = WindowsPreferenceDefinitions.All.Where(d => selected.ContainsKey(d.Id)).ToArray();
            if (changes.Length == 0) return;
            var details = string.Join("\n\n", changes.Select(d => d.Title + ": " + (selected[d.Id] ? "On" : "Off") + "\n" + d.Description + "\n" + d.Scope));
            if (changes.Any(d => d.RestartExplorer)) details += "\n\nFile Explorer will restart and open Explorer windows will close.";
            if (!await context.ConfirmAsync("Apply Windows changes?", details)) throw new ActionDeclinedException();
            PreferencesApplyResult result;
            try { result = await Task.Run(() => service.ApplyAsync(selected, ct)); }
            catch { workspace.Result = "The change did not complete. Check Activity and the restore backup before retrying."; throw; }
            try { await Refresh(); } finally { await Finish(result, false); }
        }, context, true);
        workspace.ApplyAction = page.Actions[^1]; workspace.ApplyAction.IsEnabled = false;
        page.Action("Restore previous settings", async ct =>
        {
            var note = "Restore this page's backed-up values only where they still match DevTools' last change? Later external changes are preserved.";
            if (WindowsPreferenceDefinitions.All.Any(d => d.Page == id && d.RestartExplorer)) note += "\n\nFile Explorer will restart if Explorer settings are restored; open Explorer windows will close.";
            if (!await context.ConfirmAsync("Restore previous Windows settings?", note)) throw new ActionDeclinedException();
            var result = await Task.Run(() => service.RestoreAsync(ct));
            try { await Refresh(); } finally { await Finish(result, true); }
        }, context);
        workspace.RestoreAction = page.Actions[^1]; workspace.RestoreAction.IsEnabled = false;
        page.Action("Restart Explorer", async ct =>
        {
            if (!await context.ConfirmAsync("Restart File Explorer?", "The desktop and taskbar will briefly reload. Open File Explorer windows will close.")) throw new ActionDeclinedException();
            await system.RestartExplorerAsync(ct); workspace.Result = "File Explorer restarted.";
        }, context);
        workspace.RestartAction = page.Actions[^1];
        page.Action("Open Windows settings", ct => { context.Open(definition.Settings); return Task.CompletedTask; }, context);
        workspace.SettingsAction = page.Actions[^1];
        return page;
    }
}
