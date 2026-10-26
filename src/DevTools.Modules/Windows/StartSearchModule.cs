using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed class StartSearchModule(IStartSearchBackend? backend = null) : IDevToolsModule
{
    public string Id => "start-search";
    public ModulePage CreatePage(IModuleContext context)
    {
        var workspace = new StartSearchWorkspace();
        var page = new ModulePage(Id, "Start & search", "Choose what appears when you open Start or search.", "Windows", "\uE721") { StartSearchWorkspace = workspace };
        var system = backend ?? new StartSearchRegistryBackend();
        var service = new StartSearchService(system, Path.Combine(context.DataDirectory, "StartSearch", "backup.json"));
        async Task Restart(CancellationToken ct)
        {
            workspace.Result = "Settings are saved. Restarting Explorer to refresh Start...";
            try { await system.RestartExplorerAsync(ct); workspace.Result = "WinUtil settings saved and read back; Explorer restarted. Open Start to check the Recommended section."; }
            catch { workspace.Result = "Settings are saved, but Explorer still needs a restart. Use Restart Explorer or sign out and back in."; throw; }
        }
        void Load(StartSearchSnapshot snapshot)
        {
            workspace.Platform = snapshot.Platform; workspace.CanChangeBing = snapshot.CanBing; workspace.CanChangeRecommendations = snapshot.CanRecommendations;
            workspace.BingState = snapshot.BingState; workspace.RecommendationsState = snapshot.RecommendationsState;
            workspace.BingHint = snapshot.BingHint; workspace.RecommendationsHint = snapshot.RecommendationsHint; workspace.HasBackup = snapshot.HasBackup;
            workspace.SetCurrent(snapshot.BingDisabled, snapshot.RecommendationsDisabled);
            workspace.RestoreAction!.IsEnabled = workspace.HasBackup;
        }
        page.Action("Check current settings", async ct => { Load(await Task.Run(service.Read, ct)); workspace.Result = "Saved settings checked. Choose changes, then Apply."; }, context);
        workspace.CheckAction = page.Actions[^1];
        page.Action("Apply changes", async ct =>
        {
            bool? bing = workspace.BingChanged ? workspace.DisableBing : null;
            bool? recommendations = workspace.RecommendationsChanged ? workspace.DisableRecommendations : null;
            if (recommendations.HasValue && !await context.ConfirmAsync("Apply WinUtil Start menu settings?", "This changes the Recommended section for all users. Disabling it also disables Windows Spotlight on the lock screen.\n\nExplorer will restart and open File Explorer windows will close. Apply the selected changes and restart Explorer?")) throw new ActionDeclinedException();
            var restart = await Task.Run(() => service.Apply(bing, recommendations, ct), ct);
            workspace.Result = "WinUtil settings saved and read back. Sign out and back in if Bing search does not refresh.";
            try { Load(await Task.Run(service.Read)); }
            finally { if (restart) await Restart(CancellationToken.None); }
        }, context, true);
        workspace.ApplyAction = page.Actions[^1]; workspace.ApplyAction.IsEnabled = false;
        page.Action("Restore previous settings", async ct =>
        {
            if (!await context.ConfirmAsync("Restore previous Start menu settings?", "Restore only settings still owned by DevTools? Recommendation settings can affect all users.\n\nIf recommendation values are restored, Explorer will restart and open File Explorer windows will close.")) throw new ActionDeclinedException();
            var result = await Task.Run(() => service.Restore(ct), ct);
            try { Load(await Task.Run(service.Read)); }
            finally { if (result.RestartExplorer) await Restart(CancellationToken.None); }
            workspace.Result = $"Restored {result.Restored} setting(s); preserved {result.Preserved} protected or externally changed setting(s)." + (result.RestartExplorer ? " Explorer restarted." : " Sign out if Start does not refresh.");
        }, context);
        workspace.RestoreAction = page.Actions[^1]; workspace.RestoreAction.IsEnabled = false;
        page.Action("Open Start settings", ct => { context.Open("ms-settings:personalization-start"); return Task.CompletedTask; }, context);
        workspace.SettingsAction = page.Actions[^1];
        page.Action("Restart Explorer", async ct =>
        {
            if (!await context.ConfirmAsync("Restart Explorer?", "The taskbar and desktop will briefly reload. Open File Explorer windows will close. Restart Explorer now?")) throw new ActionDeclinedException();
            await Restart(ct);
        }, context);
        workspace.RestartAction = page.Actions[^1];
        workspace.SelectionChanged += () => workspace.ApplyAction.IsEnabled = workspace.HasChanges;
        return page;
    }
}
