using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed class WindowsUpdatesModule : IDevToolsModule
{
    private readonly Func<IModuleContext, UpdatePauseController> create;
    private readonly Func<IModuleContext, UpdatePolicyController> policy;
    private readonly Func<IModuleContext, UpdateController> services;
    public WindowsUpdatesModule() : this(UpdatePauseController.Create, UpdatePolicyController.Create, UpdateController.Create) { }
    public WindowsUpdatesModule(Func<IModuleContext, UpdatePauseController> create, Func<IModuleContext, UpdatePolicyController> policy, Func<IModuleContext, UpdateController> services) => (this.create, this.policy, this.services) = (create, policy, services);
    public string Id => "windows-updates";
    public ModulePage CreatePage(IModuleContext c)
    {
        var w = new WindowsWorkspace();
        var p = new ModulePage(Id, "Update control", "Set an extended pause date in Windows Update Settings.", "Windows", "\uE895") { WindowsWorkspace = w };
        p.Add("duration", "Pause duration", "1 year", FieldKind.Choice, choices: ["1 year", "2 years", "Custom date"]);
        p.Add("restoreDate", "Pause until", DateTime.Today.AddYears(1).ToString("yyyy-MM-dd"));
        p["restoreDate"].IsVisible = false;
        p["duration"].PropertyChanged += (_, _) => p["restoreDate"].IsVisible = p["duration"].Value == "Custom date";
        var pause = create(c); var priorPolicy = policy(c); var legacy = services(c);
        bool NeedsRecovery() => priorPolicy.Plan is not null || legacy.Plan is not null || legacy.ReadServices().Any(s => s.StartType == 4);
        void Refresh()
        {
            try
            {
                var state = pause.Read(); var plan = pause.Plan; var recover = NeedsRecovery(); var liveServices = legacy.ReadServices();
                w.Edition = state.Edition;
                w.Badge = !state.Supported ? "Unavailable" : state.Conflict is not null ? "Managed elsewhere" : state.Paused ? "Paused" : "Not paused";
                w.Heading = state.Paused ? "Updates paused until " + state.EndsAt!.Value.ToLocalTime().ToString("d MMMM yyyy") + "." : "Windows updates are not paused.";
                w.Description = state.Conflict ?? "Extended pause uses the Windows Settings registry workaround. Reopen Windows Update to see the selected date; behavior can vary by Windows build.";
                if (!state.Supported) w.Description = "This Windows installation is not supported by the extended-pause control.";
                if (recover) w.Description = "An older DevTools policy or disabled update service remains. Use Restore previous settings before applying the new pause. " + w.Description;
                w.ServiceState = string.Join(" · ", liveServices.Select(s => s.Name + ": " + s.StateText));
                w.Startup = state.EndsAt is { } end ? end.ToLocalTime().ToString("g") : "No pause date";
                w.Resume = plan is not null ? "Original pause settings restore: " + plan.RestoreAt.ToLocalTime().ToString("g") + " (while signed in, or next sign-in)." : "No DevTools pause backup. Existing Windows pause settings are shown above.";
                if (plan is not null) { try { if (!pause.RecoveryScheduled) w.Resume += " Recovery task missing — restore and apply again."; } catch (Exception e) { w.Resume += " Recovery task could not be checked: " + e.Message; } }
                w.IsPositive = state.Supported && state.Conflict is null && !state.Paused && !recover;
                if (w.PauseAction is { } apply) apply.IsEnabled = state.Supported && state.Conflict is null && !recover;
                if (w.ResumeAction is { } restore) restore.IsEnabled = plan is not null || recover;
                if (w.HealthAction is { } health) health.IsEnabled = liveServices.Length == 2 && liveServices.All(s => s.Exists && s.StartType != 4);
                w.IsInitialized = true;
            }
            catch (Exception e) { w.Badge = "Unknown"; w.Heading = "Windows pause settings could not be inspected."; w.Description = e.Message; w.IsPositive = false; if (w.PauseAction is { } action) action.IsEnabled = false; }
        }
        p.Action("Refresh status", ct => { Refresh(); return Task.CompletedTask; }, c); w.RefreshAction = p.Actions[^1];
        p.Action("Restore previous settings", async ct => {
            if (!await c.ConfirmAsync("Restore Windows Update settings", "Restore the pause values saved before DevTools changed them? Any older DevTools policy plan will also be restored, and disabled update services re-enabled. Previous pause dates may still apply.")) return;
            try { pause.Restore(); priorPolicy.Restore(); if (legacy.Plan is not null || legacy.ReadServices().Any(s => s.StartType == 4)) await legacy.ResumeAsync(ct); }
            finally { Refresh(); }
        }, c); w.ResumeAction = p.Actions[^1];
        p.Action("Check update health", async ct => { w.Health = "Checking…"; try { w.Health = await legacy.CheckHealthAsync(ct); } catch { w.Health = "Check did not succeed"; throw; } finally { Refresh(); } await c.ShowReportAsync("Windows Update health", w.Health); }, c); w.HealthAction = p.Actions[^1];
        p.Action("Apply pause", async ct => {
            var end = UpdatePauseController.ResolveDeadline(p["duration"].Value, p["restoreDate"].Value, DateTimeOffset.Now);
            if (NeedsRecovery()) throw new InvalidOperationException("Restore the older DevTools update settings before applying a pause.");
            if (!await c.ConfirmAsync("Apply extended Windows pause", "Pause feature and quality updates until " + end.ToLocalTime().ToString("f") + " and extend the Settings pause limit? This registry workaround varies by Windows build. Security updates will be paused. Existing values will be backed up; services will not be stopped.")) return;
            try { await pause.ApplyAsync(end, ct); c.Log(p.Title, "Requested pause: " + end.ToString("O") + "; read back: " + pause.Read().EndsAt?.ToString("O")); }
            finally { Refresh(); }
        }, c, true); w.PauseAction = p.Actions[^1];
        p.Action("Pause and service details", async ct => { var state = pause.Read(); await c.ShowReportAsync("Windows Update settings", state.Edition + "\n" + string.Join("\n", state.Values.Select(v => v.Key + ": " + (v.Value?.Text ?? v.Value?.Number?.ToString() ?? "Not configured"))) + "\n" + w.Resume + "\n" + string.Join("\n", legacy.ReadServices().Select(s => $"{s.Name}: {s.StateText}; startup={s.StartType}"))); }, c); w.DetailsAction = p.Actions[^1];
        p.Action("Open Windows Update", ct => { c.Open("ms-settings:windowsupdate"); return Task.CompletedTask; }, c); w.SettingsAction = p.Actions[^1];
        Refresh(); return p;
    }
}
