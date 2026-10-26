using DevTools.Contracts;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed record UpdatePolicyState(bool Supported, string Edition, int? NoAutoUpdate, string? Conflict = null)
{
    public bool CanChange => Supported && Conflict is null;
    public bool AutomaticUpdatesDisabled => NoAutoUpdate == 1;
}
public sealed record UpdatePolicyPlan(Guid Token, DateTimeOffset? RestoreAt, int? OriginalValue);
public interface IUpdatePolicyBackend
{
    UpdatePolicyState Read();
    void Write(int? value);
}
public interface IUpdatePolicyStore
{
    UpdatePolicyPlan? Load();
    void Save(UpdatePolicyPlan plan);
    void Clear();
    IDisposable Lock();
}
public sealed class UpdatePolicyStore(string directory) : IUpdatePolicyStore
{
    private string PathName => Path.Combine(directory, "update-policy-plan.json");
    public static bool Valid(UpdatePolicyPlan plan) => plan.Token != Guid.Empty && plan.OriginalValue is null or 0 or 1;
    public UpdatePolicyPlan? Load()
    {
        if (!File.Exists(PathName)) return null;
        try { var plan = JsonSerializer.Deserialize<UpdatePolicyPlan>(File.ReadAllText(PathName)); return plan is not null && Valid(plan) ? plan : throw new InvalidDataException("Invalid update policy backup; keep this file for recovery."); }
        catch (JsonException e) { throw new InvalidDataException("The update policy backup is unreadable; keep it for recovery.", e); }
    }
    public void Save(UpdatePolicyPlan plan)
    {
        if (!Valid(plan)) throw new InvalidDataException("Invalid update policy backup.");
        Directory.CreateDirectory(directory); File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(plan)); File.Move(PathName + ".tmp", PathName, true);
    }
    public void Clear() { if (File.Exists(PathName)) File.Delete(PathName); }
    public IDisposable Lock() { Directory.CreateDirectory(directory); return File.Open(Path.Combine(directory, "update-policy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
}
public sealed class UpdatePolicyController(IUpdatePolicyBackend backend, IUpdateResumeScheduler scheduler, IUpdatePolicyStore store)
{
    public UpdatePolicyState Read() => backend.Read();
    public UpdatePolicyPlan? Plan => store.Load();
    public bool RecoveryScheduled => scheduler.Exists;
    public static DateTimeOffset? ResolveDeadline(string duration, string customDate, DateTimeOffset now)
    {
        if (duration == "Until re-enabled") return null;
        if (duration == "1 year") return now.AddYears(1);
        if (duration == "2 years") return now.AddYears(2);
        if (duration != "Custom date" || !DateTime.TryParseExact(customDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            throw new ArgumentException("Choose a duration or enter a date as YYYY-MM-DD.");
        var due = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
        if (due <= now || due > now.AddYears(10)) throw new ArgumentException("Choose a future date within the next 10 years.");
        return due;
    }
    public async Task ApplyAsync(DateTimeOffset? deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (deadline is { } due && (due <= DateTimeOffset.Now || due > DateTimeOffset.Now.AddYears(10))) throw new ArgumentException("The restore date must be in the future and within 10 years.");
        using var operation = store.Lock();
        var before = backend.Read();
        if (!before.CanChange) throw new InvalidOperationException(before.Conflict ?? "This Windows edition does not support this automatic-update policy.");
        var previous = store.Load();
        if (previous is not null && before.NoAutoUpdate != 1) throw new InvalidOperationException("The policy changed outside DevTools. Restore the existing plan before creating a new one.");
        var plan = new UpdatePolicyPlan(Guid.NewGuid(), deadline, previous is null ? before.NoAutoUpdate : previous.OriginalValue);
        store.Save(plan);
        var written = false;
        try
        {
            if (deadline is { } when) await scheduler.RegisterAsync(plan.Token, when, token);
            token.ThrowIfCancellationRequested();
            written = true; backend.Write(1);
            if (backend.Read().NoAutoUpdate != 1) throw new InvalidOperationException("Windows did not retain the local automatic-update policy.");
            if (deadline is null) scheduler.Remove();
        }
        catch (Exception failure)
        {
            try
            {
                if (written) { backend.Write(before.NoAutoUpdate); if (backend.Read().NoAutoUpdate != before.NoAutoUpdate) throw new InvalidOperationException("Previous policy could not be verified."); }
                if (previous is null) { scheduler.Remove(); store.Clear(); }
                else { store.Save(previous); if (previous.RestoreAt is { } oldDue) await scheduler.RegisterAsync(previous.Token, oldDue, CancellationToken.None); else scheduler.Remove(); }
            }
            catch (Exception rollback) { throw new AggregateException("The policy change failed and rollback needs attention. Keep the backup and use Restore previous settings.", failure, rollback); }
            throw;
        }
    }
    public void Restore() => RestoreCore(null, null);
    private bool RestoreCore(Guid? expectedToken, DateTimeOffset? now)
    {
        using var operation = store.Lock();
        var plan = store.Load(); if (plan is null) return false;
        if (expectedToken is { } token && (plan.Token != token || plan.RestoreAt is null || now < plan.RestoreAt)) return false;
        var state = backend.Read();
        // Do not overwrite changes made by another administrator or management tool.
        if (state.NoAutoUpdate == 1) backend.Write(plan.OriginalValue);
        else if (state.NoAutoUpdate != plan.OriginalValue) throw new InvalidOperationException("The policy changed outside DevTools. Its current value has been preserved; review the backup before restoring.");
        if (backend.Read().NoAutoUpdate != plan.OriginalValue) throw new InvalidOperationException("The previous policy could not be verified; the recovery backup was retained.");
        scheduler.Remove(); store.Clear();
        return true;
    }
    public Task<bool> RestoreDueAsync(Guid token, DateTimeOffset now)
    {
        return Task.FromResult(RestoreCore(token, now));
    }
    public static UpdatePolicyController Create(IModuleContext context) => new(new WindowsUpdatePolicyBackend(context), new UpdateResumeScheduler(context.DataDirectory), new UpdatePolicyStore(context.DataDirectory));
}
