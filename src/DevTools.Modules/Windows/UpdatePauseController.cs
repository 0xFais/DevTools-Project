using DevTools.Contracts;
using System.Globalization;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed record PauseValue(string Kind, string? Text = null, int? Number = null)
{
    public static PauseValue String(string value) => new("String", value);
    public static PauseValue Dword(int value) => new("DWord", Number: value);
}
public sealed record UpdatePauseState(bool Supported, string Edition, Dictionary<string, PauseValue?> Values, string? Conflict = null)
{
    public DateTimeOffset? EndsAt
    {
        get
        {
            var dates = new List<DateTimeOffset>();
            foreach (var name in new[] { "UX/PauseUpdatesExpiryTime", "UX/PauseFeatureUpdatesEndTime", "UX/PauseQualityUpdatesEndTime" })
            { if (!DateTimeOffset.TryParse(Values.GetValueOrDefault(name)?.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)) return null; dates.Add(end); }
            return dates.Min();
        }
    }
    public bool Paused => EndsAt > DateTimeOffset.Now && Values.GetValueOrDefault("State/PausedFeatureStatus")?.Number == 1 && Values.GetValueOrDefault("State/PausedQualityStatus")?.Number == 1;
}
public sealed record UpdatePausePlan(Guid Token, DateTimeOffset RestoreAt, Dictionary<string, PauseValue?> Original, Dictionary<string, PauseValue?> Applied);
public interface IUpdatePauseBackend { UpdatePauseState Read(); void Write(IReadOnlyDictionary<string, PauseValue?> values); }
public interface IUpdatePauseStore
{
    UpdatePausePlan? Load(); void Save(UpdatePausePlan plan); void Clear(); IDisposable Lock();
}
public sealed class UpdatePauseStore(string directory) : IUpdatePauseStore
{
    private string FileName => Path.Combine(directory, "update-pause-plan.json");
    public static bool Valid(UpdatePausePlan p) => p.Token != Guid.Empty && p.RestoreAt != default && ValidValues(p.Original) && ValidValues(p.Applied);
    public static bool ValidValues(IReadOnlyDictionary<string, PauseValue?>? values) => values is not null && values.Count == UpdatePauseController.Names.Length && values.Keys.ToHashSet().SetEquals(UpdatePauseController.Names) && values.Values.All(v => v is null || v.Kind == "String" && v.Text is not null && v.Number is null || v.Kind == "DWord" && v.Number is not null && v.Text is null);
    public UpdatePausePlan? Load()
    {
        if (!File.Exists(FileName)) return null;
        try { var plan = JsonSerializer.Deserialize<UpdatePausePlan>(File.ReadAllText(FileName)); return plan is not null && Valid(plan) ? plan : throw new InvalidDataException("Invalid pause backup; keep the file for recovery."); }
        catch (JsonException e) { throw new InvalidDataException("The pause backup is unreadable; keep it for recovery.", e); }
    }
    public void Save(UpdatePausePlan plan) { if (!Valid(plan)) throw new InvalidDataException("Invalid pause backup."); Directory.CreateDirectory(directory); File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(plan)); File.Move(FileName + ".tmp", FileName, true); }
    public void Clear() { if (File.Exists(FileName)) File.Delete(FileName); }
    // Share the policy lock: both generations use the same recovery task.
    public IDisposable Lock() { Directory.CreateDirectory(directory); return File.Open(Path.Combine(directory, "update-policy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
}
public sealed class UpdatePauseController(IUpdatePauseBackend backend, IUpdateResumeScheduler scheduler, IUpdatePauseStore store)
{
    public static readonly string[] Names = ["UX/FlightSettingsMaxPauseDays", "UX/PauseUpdatesStartTime", "UX/PauseUpdatesExpiryTime", "UX/PauseFeatureUpdatesStartTime", "UX/PauseFeatureUpdatesEndTime", "UX/PauseQualityUpdatesStartTime", "UX/PauseQualityUpdatesEndTime", "State/PausedFeatureDate", "State/PausedQualityDate", "State/PausedFeatureStatus", "State/PausedQualityStatus"];
    public UpdatePauseState Read() => backend.Read();
    public UpdatePausePlan? Plan => store.Load();
    public bool RecoveryScheduled => scheduler.Exists;
    public static DateTimeOffset ResolveDeadline(string duration, string customDate, DateTimeOffset now)
    {
        if (duration is not ("1 year" or "2 years" or "Custom date")) throw new ArgumentException("Choose 1 year, 2 years or Custom date. Windows pause needs an end date.");
        return UpdatePolicyController.ResolveDeadline(duration, customDate, now)!.Value;
    }
    public static Dictionary<string, PauseValue?> BuildValues(DateTimeOffset now, DateTimeOffset end, int? existingLimit = null)
    {
        var days = checked((int)Math.Ceiling((end - now).TotalDays));
        if (days < 1 || end > now.AddYears(10)) throw new ArgumentException("Choose a future pause date within ten years.");
        string Format(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var values = new Dictionary<string, PauseValue?>();
        foreach (var name in Names) values[name] = name.EndsWith("Status") ? PauseValue.Dword(1) : name.EndsWith("MaxPauseDays") ? PauseValue.Dword(Math.Max(days, existingLimit is >= 1 ? existingLimit.Value : 35)) : PauseValue.String(Format(name.EndsWith("EndTime") || name.EndsWith("ExpiryTime") ? end : now));
        return values;
    }
    public async Task ApplyAsync(DateTimeOffset end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var operation = store.Lock();
        var before = backend.Read();
        if (!before.Supported || before.Conflict is not null) throw new InvalidOperationException(before.Conflict ?? "Extended pause is unavailable on this Windows installation.");
        var previous = store.Load();
        if (previous is not null && !Matches(before.Values, previous.Applied)) throw new InvalidOperationException("Pause settings changed outside DevTools. Restore the existing backup before applying another date.");
        var applied = BuildValues(DateTimeOffset.Now, end, before.Values.GetValueOrDefault("UX/FlightSettingsMaxPauseDays")?.Number);
        var plan = new UpdatePausePlan(Guid.NewGuid(), end, previous?.Original ?? before.Values, applied);
        store.Save(plan); var written = false;
        try
        {
            await scheduler.RegisterAsync(plan.Token, end, token);
            token.ThrowIfCancellationRequested(); written = true; backend.Write(applied);
            if (!Matches(backend.Read().Values, applied)) throw new InvalidOperationException("Windows did not retain the requested pause date. The pause was not reported as successful.");
        }
        catch (Exception failure)
        {
            try
            {
                if (written) { backend.Write(before.Values); if (!Matches(backend.Read().Values, before.Values)) throw new InvalidOperationException("The previous pause settings could not be verified."); }
                if (previous is null) { scheduler.Remove(); store.Clear(); }
                else { store.Save(previous); await scheduler.RegisterAsync(previous.Token, previous.RestoreAt, CancellationToken.None); }
            }
            catch (Exception rollback) { throw new AggregateException("Pause failed and rollback needs attention. Keep the backup and use Restore previous settings.", failure, rollback); }
            throw;
        }
    }
    private static bool ValueMatches(string name, PauseValue? a, PauseValue? b)
    {
        if (a == b) return true;
        // Windows can normalize its status dates from ISO UTC to a space-separated UTC format.
        return !name.EndsWith("MaxPauseDays") && a?.Kind == "String" && b?.Kind == "String" &&
            DateTimeOffset.TryParse(a.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var left) &&
            DateTimeOffset.TryParse(b.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var right) && left == right;
    }
    internal static bool Matches(IReadOnlyDictionary<string, PauseValue?> a, IReadOnlyDictionary<string, PauseValue?> b) => Names.All(n => ValueMatches(n, a.GetValueOrDefault(n), b.GetValueOrDefault(n)));
    public void Restore() => RestoreCore(null, DateTimeOffset.Now);
    private bool RestoreCore(Guid? expectedToken, DateTimeOffset now)
    {
        using var operation = store.Lock(); var plan = store.Load(); if (plan is null) return false;
        if (expectedToken is { } token && (token != plan.Token || now < plan.RestoreAt)) return false;
        var current = backend.Read().Values; var restore = new Dictionary<string, PauseValue?>(current);
        foreach (var name in Names)
        {
            var value = current.GetValueOrDefault(name);
            if (ValueMatches(name, value, plan.Original[name])) continue;
            if (ValueMatches(name, value, plan.Applied[name])) { restore[name] = plan.Original[name]; continue; }
            // Windows may clear dates/status itself after expiry; preserve that cleanup.
            if (now >= plan.RestoreAt && name != "UX/FlightSettingsMaxPauseDays" && (value is null || name.EndsWith("Status") && value.Number == 0)) continue;
            throw new InvalidOperationException("Windows pause settings changed outside DevTools (" + name + "). They were preserved; the backup remains available.");
        }
        backend.Write(restore);
        if (!Matches(backend.Read().Values, restore)) throw new InvalidOperationException("Pause restoration could not be verified; the backup was retained.");
        scheduler.Remove(); store.Clear(); return true;
    }
    public Task<bool> RestoreDueAsync(Guid token, DateTimeOffset now) => Task.FromResult(RestoreCore(token, now));
    public static UpdatePauseController Create(IModuleContext context) => new(new WindowsUpdatePauseBackend(context), new UpdateResumeScheduler(context.DataDirectory), new UpdatePauseStore(context.DataDirectory));
}
