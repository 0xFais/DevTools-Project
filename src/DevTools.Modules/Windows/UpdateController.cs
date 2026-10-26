using DevTools.Contracts;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed record UpdateServiceState(string Name, int StartType, int State)
{
    public bool Exists => StartType is >= 2 and <= 4;
    public string StateText => State switch { 1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 7 => "Paused", _ => "Unknown" };
}
public sealed record UpdatePlan(Guid Token, string Mode, DateTimeOffset? ResumeAt, UpdateServiceState[] Original);
public sealed record UpdateSnapshot(UpdateServiceState[] Services, UpdatePlan? Plan, bool RecoveryScheduled)
{
    public bool Enabled => Services.Length == 2 && Services.All(s => s.Exists && s.StartType != 4);
    public bool Disabled => Services.Length == 2 && Services.All(s => s.StartType == 4 && s.State == 1);
    public bool Unknown => Services.Length != 2 || Services.Any(s => !s.Exists || s.State == 0);
    public string Mode => Unknown ? "Unknown" : Enabled ? "Enabled" : !Disabled ? "Partially disabled" : Plan?.Mode == "Paused" && RecoveryScheduled ? "Paused" : "Disabled";
}
public interface IWindowsUpdateServices
{
    UpdateServiceState[] Read();
    Task SetStartAsync(string service, int startType, CancellationToken token);
    Task StopAsync(string service, CancellationToken token);
    Task StartAsync(string service, CancellationToken token);
    Task<string> CheckHealthAsync(CancellationToken token);
}
public interface IUpdateResumeScheduler
{
    bool Exists { get; }
    Task RegisterAsync(Guid token, DateTimeOffset resumeAt, CancellationToken cancellationToken);
    void Remove();
}
public interface IUpdatePlanStore
{
    UpdatePlan? Load();
    void Save(UpdatePlan plan);
    void Clear();
}
public sealed class UpdatePlanStore(string directory) : IUpdatePlanStore
{
    private readonly string path = Path.Combine(directory, "update-plan.json");
    public UpdatePlan? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path));
            return plan is not null && UpdateController.Valid(plan) ? plan : null;
        }
        catch (JsonException) { return null; }
    }
    public void Save(UpdatePlan plan)
    {
        if (!UpdateController.Valid(plan)) throw new InvalidDataException("Invalid update recovery plan.");
        Directory.CreateDirectory(directory); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(plan)); File.Move(path + ".tmp", path, true);
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

/// <summary>Per-user recovery journal and scheduled restore for service-based update pauses.</summary>
public sealed class UpdateController(IWindowsUpdateServices services, IUpdateResumeScheduler scheduler, IUpdatePlanStore store)
{
    public static readonly string[] Names = ["wuauserv", "UsoSvc"];
    public static bool Valid(UpdatePlan plan) => plan.Token != Guid.Empty && plan.Mode is "Paused" or "Disabled"
        && plan.Original.Length == 2 && plan.Original.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Names)
        && plan.Original.All(s => s.StartType is >= 2 and <= 4) && (plan.Mode != "Paused" || plan.ResumeAt is not null);
    public UpdateSnapshot Read() => new(services.Read(), store.Load(), scheduler.Exists);
    public UpdateServiceState[] ReadServices() => services.Read();
    public UpdatePlan? Plan => store.Load();
    public Task<string> CheckHealthAsync(CancellationToken token) => services.CheckHealthAsync(token);
    public async Task SetAsync(int? days, CancellationToken token)
    {
        if (days is not null && days is not (7 or 14 or 28 or 35)) throw new ArgumentOutOfRangeException(nameof(days));
        var before = services.Read();
        if (before.Length != 2 || before.Any(s => !s.Exists || s.State == 0)) throw new InvalidOperationException("The required Windows Update services could not be inspected.");
        var previous = store.Load();
        var plan = new UpdatePlan(Guid.NewGuid(), days is null ? "Disabled" : "Paused", days is null ? null : DateTimeOffset.UtcNow.AddDays(days.Value), previous?.Original ?? before);
        store.Save(plan);
        var touched = false;
        try
        {
            if (plan.ResumeAt is { } due) await scheduler.RegisterAsync(plan.Token, due, token);
            touched = true;
            await DisableAndVerifyAsync(token);
            if (plan.ResumeAt is null) scheduler.Remove();
        }
        catch (Exception failure)
        {
            // Roll back a failed change. Keep a recovery plan if rollback cannot complete.
            try
            {
                if (touched) foreach (var state in before) { await services.SetStartAsync(state.Name, state.StartType, CancellationToken.None); if (state.State == 4) await services.StartAsync(state.Name, CancellationToken.None); else if (state.State == 1) await services.StopAsync(state.Name, CancellationToken.None); }
                if (touched)
                {
                    var restored = services.Read();
                    if (before.Any(original => !restored.Any(s => s.Name == original.Name && s.StartType == original.StartType && (original.State is not (1 or 4) || s.State == original.State))))
                        throw new InvalidOperationException("The previous service state could not be verified: " + Describe(restored));
                }
                if (previous is null) { scheduler.Remove(); store.Clear(); }
                else { store.Save(previous); if (previous.ResumeAt is { } oldDue) await scheduler.RegisterAsync(previous.Token, oldDue, CancellationToken.None); else scheduler.Remove(); }
            }
            catch (Exception rollback) { throw new AggregateException("The update change failed and rollback could not complete. Use Enable / resume updates to recover manually.", failure, rollback); }
            if (failure is UpdateVerificationException)
                throw new InvalidOperationException(failure.Message + "\nThe previous service configuration was restored. Open Service details for the current state. Windows or another update manager may be restoring these services.", failure);
            throw;
        }
    }
    private sealed class UpdateVerificationException(string message) : InvalidOperationException(message);
    private async Task DisableAndVerifyAsync(CancellationToken token)
    {
        var observations = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // Disable both startup configurations before stopping either service.
            // Stop the orchestrator first so it cannot restart Windows Update mid-change.
            foreach (var name in Names) await services.SetStartAsync(name, 4, token);
            foreach (var name in Names.Reverse()) await services.StopAsync(name, token);
            var after = services.Read();
            observations.Add($"Attempt {attempt}: " + Describe(after));
            if (!IsStoppedAndDisabled(after)) continue;
            await Task.Delay(500, token);
            after = services.Read();
            observations.Add($"Attempt {attempt}, follow-up: " + Describe(after));
            if (IsStoppedAndDisabled(after)) return;
        }
        throw new UpdateVerificationException("Windows Update could not remain stopped and disabled after three attempts.\n" + string.Join("\n", observations));
    }
    private static bool IsStoppedAndDisabled(UpdateServiceState[] states) => states.Length == 2 && states.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Names) && states.All(s => s.StartType == 4 && s.State == 1);
    private static string Describe(UpdateServiceState[] states) => string.Join("; ", states.Select(s => $"{s.Name}: {s.StateText} (state={s.State}), startup={s.StartType switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown" }} ({s.StartType})"));
    public async Task ResumeAsync(CancellationToken token)
    {
        var plan = store.Load();
        foreach (var name in Names)
        {
            var original = plan?.Original.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var start = original?.StartType is 2 or 3 ? original.StartType : name == "wuauserv" ? 3 : 2;
            await services.SetStartAsync(name, start, token);
            await services.StartAsync(name, token);
        }
        scheduler.Remove(); store.Clear();
    }
    public async Task<bool> ResumeDueAsync(Guid token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var plan = store.Load();
        if (plan is null || plan.Token != token || plan.Mode != "Paused" || plan.ResumeAt is null || now < plan.ResumeAt) return false;
        await ResumeAsync(cancellationToken); return true;
    }
    public static UpdateController Create(IModuleContext context) => new(new WindowsUpdateServices(context), new UpdateResumeScheduler(context.DataDirectory), new UpdatePlanStore(context.DataDirectory));
}
