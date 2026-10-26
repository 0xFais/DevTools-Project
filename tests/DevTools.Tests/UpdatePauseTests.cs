using DevTools.Contracts;
using DevTools.Modules.Windows;
using System.IO;
using static DevTools.Tests.WindowsControlTests;

namespace DevTools.Tests;

internal static class UpdatePauseTests
{
    internal static void RunAll(string directory, Action<string, Action> run)
    {
        run("Extended pause writes exact one/two-year Settings dates and the required limit", () => {
            foreach (var years in new[] { 1, 2 }) {
                var f = new Fixture(directory); var now = DateTimeOffset.Now; var end = UpdatePauseController.ResolveDeadline(years + (years == 1 ? " year" : " years"), "", now);
                f.Controller.ApplyAsync(end, default).GetAwaiter().GetResult(); var state = f.Controller.Read();
                Check(state.Paused && Math.Abs((state.EndsAt!.Value - end).TotalSeconds) < 1);
                Check(state.Values["UX/FlightSettingsMaxPauseDays"]!.Number >= Math.Ceiling((end - now).TotalDays));
                Check(new[] { "UX/PauseUpdatesExpiryTime", "UX/PauseFeatureUpdatesEndTime", "UX/PauseQualityUpdatesEndTime" }.All(n => state.Values[n]!.Text == end.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
                Check(f.Services.Mutations == 0 && f.PolicyBackend.Writes == 0 && f.Scheduler.RegisteredBeforeWrite);
            }
        });
        run("Extended pause uses UTC timestamps across offsets and leap years", () => {
            var now = new DateTimeOffset(2027, 3, 1, 0, 15, 0, TimeSpan.FromHours(3)); var end = now.AddYears(1); var values = UpdatePauseController.BuildValues(now, end);
            Check(values["UX/FlightSettingsMaxPauseDays"]!.Number == 366 && values["UX/PauseUpdatesStartTime"]!.Text == "2027-02-28T21:15:00Z" && values["UX/PauseQualityUpdatesEndTime"]!.Text == "2028-02-29T21:15:00Z");
        });
        run("Windows-normalized UTC dates remain owned and can be restored", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult();
            foreach (var name in new[] { "State/PausedFeatureDate", "State/PausedQualityDate" }) f.Backend.Values[name] = PauseValue.String(DateTimeOffset.Parse(f.Backend.Values[name]!.Text!).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
            f.Controller.Restore(); Check(f.Store.Value is null && f.Backend.Values.Values.All(v => v is null));
        });
        run("Displayed pause uses the earliest feature or quality expiry", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(2), default).GetAwaiter().GetResult();
            var shorter = DateTimeOffset.Now.AddDays(30); f.Backend.Values["UX/PauseQualityUpdatesEndTime"] = PauseValue.String(shorter.ToString("O"));
            Check(Math.Abs((f.Controller.Read().EndsAt!.Value - shorter).TotalSeconds) < 1);
        });
        run("Extended pause validates custom dates and rejects a fictitious indefinite pause", () => {
            var now = DateTimeOffset.Now; var end = UpdatePauseController.ResolveDeadline("Custom date", DateTime.Today.AddYears(2).ToString("yyyy-MM-dd"), now); Check(end.Date == DateTime.Today.AddYears(2));
            foreach (var duration in new[] { "Until re-enabled", "Invalid" }) Throws<ArgumentException>(() => UpdatePauseController.ResolveDeadline(duration, "", now));
            Throws<ArgumentException>(() => UpdatePauseController.ResolveDeadline("Custom date", "2020-01-01", now));
            Throws<ArgumentException>(() => UpdatePauseController.BuildValues(now, now.AddYears(11)));
        });
        run("Pause restoration preserves all original values, types and absence", () => {
            var f = new Fixture(directory); f.Backend.Values["UX/FlightSettingsMaxPauseDays"] = PauseValue.Dword(1200); f.Backend.Values["UX/PauseUpdatesExpiryTime"] = PauseValue.String("2026-01-01T00:00:00Z");
            var original = new Dictionary<string, PauseValue?>(f.Backend.Values); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult();
            Check(f.Backend.Values["UX/FlightSettingsMaxPauseDays"]!.Number == 1200); f.Controller.Restore();
            Check(UpdatePauseController.Matches(f.Backend.Values, original) && f.Store.Value is null && !f.Scheduler.Exists);
        });
        run("Reapplying a pause preserves the first backup and replaces the recovery token", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var first = f.Store.Value!;
            f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(2), default).GetAwaiter().GetResult(); Check(f.Store.Value!.Token != first.Token && UpdatePauseController.Matches(first.Original, f.Store.Value.Original));
            Check(f.Scheduler.Token == f.Store.Value.Token && f.Scheduler.Due == f.Store.Value.RestoreAt);
        });
        run("Pause recovery rejects stale/early tokens and restores a matching due plan", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var plan = f.Store.Value!;
            Check(!f.Controller.RestoreDueAsync(Guid.NewGuid(), plan.RestoreAt).GetAwaiter().GetResult()); Check(!f.Controller.RestoreDueAsync(plan.Token, plan.RestoreAt.AddSeconds(-1)).GetAwaiter().GetResult());
            Check(f.Controller.RestoreDueAsync(plan.Token, plan.RestoreAt).GetAwaiter().GetResult() && f.Store.Value is null && !f.Scheduler.Exists);
        });
        run("Pause scheduler failure makes no registry or service changes", () => {
            var f = new Fixture(directory); f.Scheduler.FailRegister = true; Throws<IOException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult());
            Check(f.Backend.Writes == 0 && f.Services.Mutations == 0 && f.Store.Value is null);
        });
        run("A partial pause write fails verification and rolls back without a success state", () => {
            var f = new Fixture(directory); f.Backend.PartialNext = true; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult());
            Check(!f.Controller.Read().Paused && f.Backend.Values.Values.All(v => v is null) && f.Store.Value is null);
        });
        run("Failed pause extension restores the previous active pause and schedule", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var first = f.Store.Value!; f.Backend.PartialNext = true;
            Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(2), default).GetAwaiter().GetResult());
            Check(f.Store.Value == first && UpdatePauseController.Matches(f.Backend.Values, first.Applied) && f.Scheduler.Token == first.Token);
        });
        run("External pause changes are preserved and retain their recovery backup", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); f.Backend.Values["UX/PauseUpdatesExpiryTime"] = PauseValue.String("2034-01-01T00:00:00Z");
            Throws<InvalidOperationException>(() => f.Controller.Restore()); Check(f.Backend.Values["UX/PauseUpdatesExpiryTime"]!.Text == "2034-01-01T00:00:00Z" && f.Store.Value is not null);
        });
        run("Expired pause recovery preserves automatic Windows cleanup", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var plan = f.Store.Value!;
            foreach (var name in UpdatePauseController.Names.Where(n => n != "UX/FlightSettingsMaxPauseDays")) f.Backend.Values[name] = name.EndsWith("Status") ? PauseValue.Dword(0) : null;
            Check(f.Controller.RestoreDueAsync(plan.Token, plan.RestoreAt).GetAwaiter().GetResult() && f.Store.Value is null && f.Backend.Values["State/PausedFeatureStatus"]!.Number == 0);
        });
        run("Pause backups round-trip, lock and reject unknown registry locations", () => {
            var path = Path.Combine(directory, "pause-backup-" + Guid.NewGuid()); var store = new UpdatePauseStore(path); var original = Empty(); var plan = new UpdatePausePlan(Guid.NewGuid(), DateTimeOffset.Now.AddYears(1), original, UpdatePauseController.BuildValues(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1)));
            using (store.Lock()) { store.Save(plan); Check(UpdatePauseController.Matches(store.Load()!.Applied, plan.Applied)); Throws<IOException>(() => store.Lock()); }
            var bad = new Dictionary<string, PauseValue?>(original) { ["UX/ArbitraryValue"] = PauseValue.Dword(1) }; Throws<InvalidDataException>(() => store.Save(plan with { Original = bad }));
            File.WriteAllText(Path.Combine(path, "update-pause-plan.json"), "bad JSON"); Throws<InvalidDataException>(() => store.Load());
        });
        run("Pause UI confirms the actual Settings date without changing services or policy", () => {
            var f = new Fixture(directory); var page = f.Page(); page["duration"].Value = "Custom date"; Check(page["restoreDate"].IsVisible);
            f.Context.Approve = false; Act(page, page.WindowsWorkspace!.PauseAction!, f.Context); Check(f.Backend.Writes == 0);
            f.Context.Approve = true; page["duration"].Value = "2 years"; Act(page, page.WindowsWorkspace.PauseAction!, f.Context);
            Check(page.WindowsWorkspace.Badge == "Paused" && page.WindowsWorkspace.Heading.Contains(DateTime.Today.AddYears(2).ToString("d MMMM yyyy")) && f.Services.Mutations == 0 && f.PolicyBackend.Writes == 0);
            Act(page, page.WindowsWorkspace.ResumeAction!, f.Context); Check(f.Backend.Values.Values.All(v => v is null));
        });
        run("Older DevTools policy must be restored explicitly before a Settings pause", () => {
            var f = new Fixture(directory); f.Policy.ApplyAsync(null, default).GetAwaiter().GetResult(); var page = f.Page(); Check(!page.WindowsWorkspace!.PauseAction!.CanExecute(null));
            Act(page, page.WindowsWorkspace.ResumeAction!, f.Context); Check(f.PolicyBackend.Value is null && f.Policy.Plan is null && page.WindowsWorkspace.PauseAction.CanExecute(null));
        });
        run("Extended pause is blocked by management restrictions and unsupported installations", () => {
            var f = new Fixture(directory); f.Backend.Conflict = "Managed"; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult());
            f.Backend.Conflict = null; f.Backend.Supported = false; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult()); Check(f.Backend.Writes == 0);
        });
    }
    internal static ModulePage Preview(string dir, string state)
    {
        var f = new Fixture(dir);
        if (state is "paused" or "disabled") f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(state == "paused" ? 2 : 1), default).GetAwaiter().GetResult();
        if (state == "partial") { f.Backend.Supported = false; f.Backend.Edition = "Windows Server"; }
        var page = f.Page(); if (state == "custom") page["duration"].Value = "Custom date"; return page;
    }
    private static Dictionary<string, PauseValue?> Empty() => UpdatePauseController.Names.ToDictionary(n => n, _ => (PauseValue?)null);
    private sealed class Fixture
    {
        internal readonly FakeContext Context; internal readonly FakeServices Services = new(); internal readonly Backend Backend = new(); internal readonly Store Store = new(); internal readonly Scheduler Scheduler;
        internal readonly UpdatePauseController Controller; internal readonly PolicyBackend PolicyBackend = new(); internal readonly PolicyStore PolicyStore = new(); internal readonly UpdatePolicyController Policy;
        internal Fixture(string dir) { Context = new(Path.Combine(dir, "pause-" + Guid.NewGuid())); Scheduler = new(Backend); Controller = new(Backend, Scheduler, Store); Policy = new(PolicyBackend, new FakeScheduler(Services), PolicyStore); }
        internal ModulePage Page() => new WindowsUpdatesModule(_ => Controller, _ => Policy, _ => new UpdateController(Services, new FakeScheduler(Services), new FakeStore())).CreatePage(Context);
    }
    private sealed class Backend : IUpdatePauseBackend
    {
        internal Dictionary<string, PauseValue?> Values = Empty(); internal int Writes; internal bool PartialNext, Supported = true; internal string Edition = "Windows 11 Home"; internal string? Conflict;
        public UpdatePauseState Read() => new(Supported, Edition, new(Values), Conflict);
        public void Write(IReadOnlyDictionary<string, PauseValue?> values) { Writes++; if (PartialNext) { PartialNext = false; Values["UX/FlightSettingsMaxPauseDays"] = values["UX/FlightSettingsMaxPauseDays"]; } else Values = new(values); }
    }
    private sealed class Store : IUpdatePauseStore
    {
        internal UpdatePausePlan? Value; public UpdatePausePlan? Load() => Value; public void Save(UpdatePausePlan plan) => Value = plan; public void Clear() => Value = null; public IDisposable Lock() => new Scope();
    }
    private sealed class Scope : IDisposable { public void Dispose() { } }
    private sealed class Scheduler(Backend backend) : IUpdateResumeScheduler
    {
        public bool Exists { get; private set; } internal bool FailRegister, RegisteredBeforeWrite; internal Guid Token; internal DateTimeOffset Due;
        public Task RegisterAsync(Guid token, DateTimeOffset due, CancellationToken ct) { if (FailRegister) throw new IOException("Scheduler unavailable"); Token = token; Due = due; Exists = true; RegisteredBeforeWrite = backend.Writes == 0; return Task.CompletedTask; }
        public void Remove() => Exists = false;
    }
    private sealed class PolicyBackend : IUpdatePolicyBackend { internal int? Value; internal int Writes; public UpdatePolicyState Read() => new(true, "Windows 11 Pro", Value); public void Write(int? value) { Value = value; Writes++; } }
    private sealed class PolicyStore : IUpdatePolicyStore { internal UpdatePolicyPlan? Value; public UpdatePolicyPlan? Load() => Value; public void Save(UpdatePolicyPlan plan) => Value = plan; public void Clear() => Value = null; public IDisposable Lock() => new Scope(); }
    private static void Act(ModulePage page, ModuleAction action, FakeContext context) { action.Execute(null); if (!SpinWait.SpinUntil(() => !context.IsOperationRunning, 10000) || page.Status.StartsWith("Failed")) throw new Exception(page.Status + "\n" + context.Report); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Pause assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
