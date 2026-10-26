using DevTools.Contracts;
using DevTools.Modules.Windows;
using System.IO;
using static DevTools.Tests.WindowsControlTests;

namespace DevTools.Tests;

internal static class UpdatePolicyTests
{
    internal static void RunAll(string directory, Action<string, Action> run)
    {
        run("Policy duration supports calendar years, indefinite and validated custom dates", () => {
            var now = DateTimeOffset.Now;
            Check(UpdatePolicyController.ResolveDeadline("1 year", "", now) == now.AddYears(1));
            Check(UpdatePolicyController.ResolveDeadline("2 years", "", now) == now.AddYears(2));
            Check(UpdatePolicyController.ResolveDeadline("Until re-enabled", "", now) is null);
            Check(UpdatePolicyController.ResolveDeadline("Custom date", DateTime.Today.AddDays(10).ToString("yyyy-MM-dd"), now)!.Value.Date == DateTime.Today.AddDays(10));
            foreach (var date in new[] { "not a date", "2020-01-01", DateTime.Today.AddYears(11).ToString("yyyy-MM-dd") }) Throws<ArgumentException>(() => UpdatePolicyController.ResolveDeadline("Custom date", date, now));
        });
        run("Policy support rejects Home, Server and unknown editions", () => {
            foreach (var id in new[] { "Professional", "Enterprise", "Education", "EnterpriseS", "IoTEnterpriseS" }) Check(WindowsUpdatePolicyBackend.Supports(new(id, 26100, "Windows")));
            foreach (var id in new[] { "Core", "CoreSingleLanguage", "Unknown" }) Check(!WindowsUpdatePolicyBackend.Supports(new(id, 26100, "Windows")));
            Check(!WindowsUpdatePolicyBackend.Supports(new("Enterprise", 26100, "Server", "Server")));
        });
        run("Timed policy schedules recovery before writing without service mutations", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult();
            Check(f.Backend.Value == 1 && f.Scheduler.Exists && f.Scheduler.BeforeWrite && f.Services.Mutations == 0 && f.Store.Value!.OriginalValue is null);
        });
        run("Policy restoration preserves missing, enabled and disabled original values", () => {
            foreach (int? original in new int?[] { null, 0, 1 }) { var f = new Fixture(directory); f.Backend.Value = original; f.Controller.ApplyAsync(null, default).GetAwaiter().GetResult(); f.Controller.Restore(); Check(f.Backend.Value == original && f.Store.Value is null && !f.Scheduler.Exists && f.Services.Mutations == 0); }
        });
        run("Changing policy duration preserves the first backup and cancels indefinite schedules", () => {
            var f = new Fixture(directory); f.Backend.Value = 0; f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var first = f.Store.Value!.Token;
            f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(2), default).GetAwaiter().GetResult(); Check(f.Store.Value!.OriginalValue == 0 && f.Store.Value.Token != first);
            f.Controller.ApplyAsync(null, default).GetAwaiter().GetResult(); Check(!f.Scheduler.Exists && f.Store.Value!.RestoreAt is null && f.Store.Value.OriginalValue == 0);
        });
        run("Policy recovery rejects early and stale tokens and restores only a due plan", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var plan = f.Store.Value!;
            Check(!f.Controller.RestoreDueAsync(Guid.NewGuid(), plan.RestoreAt!.Value.AddDays(1)).GetAwaiter().GetResult());
            Check(!f.Controller.RestoreDueAsync(plan.Token, plan.RestoreAt.Value.AddSeconds(-1)).GetAwaiter().GetResult());
            Check(f.Controller.RestoreDueAsync(plan.Token, plan.RestoreAt.Value).GetAwaiter().GetResult() && f.Backend.Value is null && f.Store.Value is null);
        });
        run("Policy scheduler failure makes no policy or service changes", () => {
            var f = new Fixture(directory); f.Scheduler.FailRegister = true; Throws<IOException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult()); Check(f.Backend.Writes == 0 && f.Services.Mutations == 0 && f.Store.Value is null);
        });
        run("Failed policy verification restores the original value and previous schedule", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(1), default).GetAwaiter().GetResult(); var first = f.Store.Value!;
            f.Backend.RejectNext = true; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(DateTimeOffset.Now.AddYears(2), default).GetAwaiter().GetResult());
            Check(f.Store.Value == first && f.Backend.Value == 1 && f.Scheduler.Token == first.Token);
        });
        run("Unsupported and managed Windows policies are not modified", () => {
            var f = new Fixture(directory); f.Backend.Supported = false; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(null, default).GetAwaiter().GetResult());
            f.Backend.Supported = true; f.Backend.Conflict = "Managed by organization"; Throws<InvalidOperationException>(() => f.Controller.ApplyAsync(null, default).GetAwaiter().GetResult()); Check(f.Backend.Writes == 0 && f.Store.Value is null);
        });
        run("External policy changes are preserved instead of overwritten on restore", () => {
            var f = new Fixture(directory); f.Controller.ApplyAsync(null, default).GetAwaiter().GetResult(); f.Backend.Value = 0;
            Throws<InvalidOperationException>(() => f.Controller.Restore()); Check(f.Backend.Value == 0 && f.Store.Value is not null);
        });
        run("Policy backup round-trips and corrupt backups block changes", () => {
            var path = Path.Combine(directory, "policy-store-" + Guid.NewGuid()); var store = new UpdatePolicyStore(path); var plan = new UpdatePolicyPlan(Guid.NewGuid(), DateTimeOffset.Now.AddYears(1), null);
            using (store.Lock()) { store.Save(plan); Check(store.Load() == plan); Throws<IOException>(() => store.Lock()); }
            File.WriteAllText(Path.Combine(path, "update-policy-plan.json"), "broken"); Throws<InvalidDataException>(() => store.Load());
        });
    }
    internal static ModulePage Preview(string directory, string state) => UpdatePauseTests.Preview(directory, state);
    private sealed class Fixture
    {
        internal readonly FakeContext Context; internal readonly FakeServices Services = new(); internal readonly Backend Backend = new(); internal readonly Store Store = new(); internal readonly Scheduler Scheduler; internal readonly UpdatePolicyController Controller;
        internal Fixture(string dir) { Context = new(Path.Combine(dir, "policy-" + Guid.NewGuid())); Scheduler = new(Backend); Controller = new(Backend, Scheduler, Store); }
    }
    private sealed class Backend : IUpdatePolicyBackend
    {
        internal int? Value; internal int Writes; internal bool Supported = true, RejectNext; internal string Edition = "Windows 11 Pro"; internal string? Conflict;
        public UpdatePolicyState Read() => new(Supported, Edition, Value, Conflict);
        public void Write(int? value) { Writes++; if (RejectNext) { RejectNext = false; Value = 0; } else Value = value; }
    }
    private sealed class Store : IUpdatePolicyStore
    {
        internal UpdatePolicyPlan? Value;
        public UpdatePolicyPlan? Load() => Value; public void Save(UpdatePolicyPlan plan) => Value = plan; public void Clear() => Value = null;
        public IDisposable Lock() => new Scope(); private sealed class Scope : IDisposable { public void Dispose() { } }
    }
    private sealed class Scheduler(Backend backend) : IUpdateResumeScheduler
    {
        public bool Exists { get; set; } internal bool FailRegister, BeforeWrite; internal Guid Token;
        public Task RegisterAsync(Guid token, DateTimeOffset due, CancellationToken ct) { if (FailRegister) throw new IOException("Scheduler unavailable"); BeforeWrite = backend.Writes == 0; Exists = true; Token = token; return Task.CompletedTask; }
        public void Remove() => Exists = false;
    }
    private static void Act(ModulePage page, ModuleAction action, FakeContext context) { action.Execute(null); if (!SpinWait.SpinUntil(() => !context.IsOperationRunning, 10000) || page.Status.StartsWith("Failed")) throw new Exception(page.Status + "\n" + context.Report); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Policy assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
