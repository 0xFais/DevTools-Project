using DevTools.Contracts;
using DevTools.Modules.Windows;
using DevTools.Infrastructure;
using System.IO;

namespace DevTools.Tests;

internal static class WindowsControlTests
{
    internal static void ReadOnlySmoke(string directory)
    {
        var f = new Fixture(directory); f.Context.NativeProcesses = true;
        try { var license = new WindowsActivationBackend(f.Context).ReadLicenseAsync(default).GetAwaiter().GetResult(); Console.WriteLine("Native license read: " + license.Status); }
        catch (Exception e) { Console.WriteLine("Native license read could not complete in this session: " + e.Message); }
        try { Console.WriteLine("Recovery task exists: " + new UpdateResumeScheduler(f.Context.DataDirectory).Exists); }
        catch (Exception e) { Console.WriteLine("Native task inspection could not complete in this session: " + e.Message); }
        foreach (var state in new WindowsUpdateServices(f.Context).Read()) Console.WriteLine($"{state.Name}: {state.StateText}; startup={state.StartType}");
        Console.WriteLine("Read-only inspection finished; no service, task or licensing changes requested.");
    }
    internal static void RunAll(string directory, Action<string, Action> run)
    {
        run("Missing recovery tasks are safe for inspection and cleanup without hiding real failures", () => {
            Check(!UpdateResumeScheduler.TryTaskOperation(() => throw new FileNotFoundException()));
            Check(!UpdateResumeScheduler.TryTaskOperation(() => throw new System.Runtime.InteropServices.COMException("Missing task", unchecked((int)0x80070002))));
            Check(UpdateResumeScheduler.TryTaskOperation(() => { }));
            Throws<UnauthorizedAccessException>(() => UpdateResumeScheduler.TryTaskOperation(() => throw new UnauthorizedAccessException()));
            Throws<System.Runtime.InteropServices.COMException>(() => UpdateResumeScheduler.TryTaskOperation(() => throw new System.Runtime.InteropServices.COMException("Scheduler unavailable", unchecked((int)0x800706BA))));
        });
        run("Activation license JSON ignores separate PowerShell progress diagnostics", () => {
            var f = new Fixture(directory); f.Context.Stdout = "[{\"Name\":\"Windows Pro\",\"Description\":\"Test\",\"LicenseStatus\":1,\"PartialProductKey\":\"TEST1\"}]";
            f.Context.Output = f.Context.Stdout + "\n#< CLIXML\n<Objs>Progress</Objs>";
            var license = new WindowsActivationBackend(f.Context).ReadLicenseAsync(default).GetAwaiter().GetResult(); Check(license.Activated && license.Status == "Activated");
        });
        run("Update health JSON ignores diagnostic stderr and preserves failure exit codes", () => {
            var f = new Fixture(directory); f.Context.Stdout = "{\"Code\":2,\"Count\":3}"; f.Context.Output = f.Context.Stdout + "\n#< CLIXML\n<Objs>Progress</Objs>";
            var services = new WindowsUpdateServices(f.Context); Check(services.CheckHealthAsync(default).GetAwaiter().GetResult().Contains("3 available updates"));
            f.Context.ExitCode = 1; Throws<InvalidOperationException>(() => services.CheckHealthAsync(default).GetAwaiter().GetResult());
        });
        run("Timed service pause registers recovery before stopping services", () => {
            var f = new Fixture(directory); f.Controller.SetAsync(7, default).GetAwaiter().GetResult();
            Check(f.Scheduler.Exists && f.Store.Plan is { Mode: "Paused", ResumeAt: not null } && f.Controller.Read().Mode == "Paused");
            Check(f.Services.States.All(s => s.StartType == 4 && s.State == 1) && f.Scheduler.RegisteredBeforeServiceChange);
            Check(f.Services.Operations.Take(4).SequenceEqual(new[] { "config:wuauserv", "config:UsoSvc", "stop:UsoSvc", "stop:wuauserv" }));
        });
        run("Disable retries a service re-enabled during the change", () => {
            var f = new Fixture(directory); f.Services.ReenableOnStoppedRead = 1;
            f.Controller.SetAsync(null, default).GetAwaiter().GetResult();
            Check(f.Controller.Read().Disabled && f.Store.Plan is { Mode: "Disabled" } && f.Services.Operations.Count(x => x == "config:wuauserv") == 2);
        });
        run("Disable detects a service revival during follow-up verification", () => {
            var f = new Fixture(directory); f.Services.ReenableOnStoppedRead = 2;
            f.Controller.SetAsync(7, default).GetAwaiter().GetResult();
            Check(f.Controller.Read().Mode == "Paused" && f.Services.Operations.Count(x => x == "config:wuauserv") == 2);
        });
        run("Persistent service restoration is bounded, diagnosed and rolled back", () => {
            var f = new Fixture(directory); f.Services.AlwaysReenable = true;
            try { f.Controller.SetAsync(null, default).GetAwaiter().GetResult(); throw new Exception("Expected failure."); }
            catch (InvalidOperationException e) { Check(e.Message.Contains("three attempts") && e.Message.Contains("wuauserv: Running") && e.Message.Contains("Manual (3)") && e.Message.Contains("previous service configuration was restored")); }
            Check(f.Services.Operations.Count(x => x == "config:wuauserv") == 4 && f.Store.Plan is null && !f.Scheduler.Exists && f.Controller.Read().Enabled);
        });
        run("Recovery tokens reject early and stale scheduled invocations", () => {
            var f = new Fixture(directory); f.Controller.SetAsync(14, default).GetAwaiter().GetResult(); var plan = f.Store.Plan!; var count = f.Services.Mutations;
            Check(!f.Controller.ResumeDueAsync(Guid.NewGuid(), plan.ResumeAt!.Value.AddDays(1), default).GetAwaiter().GetResult());
            Check(!f.Controller.ResumeDueAsync(plan.Token, plan.ResumeAt.Value.AddSeconds(-1), default).GetAwaiter().GetResult()); Check(f.Services.Mutations == count);
        });
        run("Due recovery enables services and clears the task and journal", () => {
            var f = new Fixture(directory); f.Controller.SetAsync(7, default).GetAwaiter().GetResult(); var plan = f.Store.Plan!;
            Check(f.Controller.ResumeDueAsync(plan.Token, plan.ResumeAt!.Value, default).GetAwaiter().GetResult()); Check(f.Controller.Read().Enabled && !f.Scheduler.Exists && f.Store.Plan is null);
        });
        run("Extending a pause preserves original startup types", () => {
            var f = new Fixture(directory); f.Controller.SetAsync(7, default).GetAwaiter().GetResult(); var first = f.Store.Plan!;
            f.Controller.SetAsync(35, default).GetAwaiter().GetResult(); Check(f.Store.Plan!.Original.All(s => s.StartType != 4) && f.Store.Plan.Token != first.Token && f.Store.Plan.ResumeAt > first.ResumeAt);
        });
        run("Indefinite disable removes automatic resume and manual resume restores defaults", () => {
            var f = new Fixture(directory); f.Controller.SetAsync(7, default).GetAwaiter().GetResult(); f.Controller.SetAsync(null, default).GetAwaiter().GetResult();
            Check(!f.Scheduler.Exists && f.Store.Plan is { Mode: "Disabled", ResumeAt: null }); f.Controller.ResumeAsync(default).GetAwaiter().GetResult(); Check(f.Controller.Read().Enabled);
        });
        run("Task-registration failure leaves service state unchanged", () => {
            var f = new Fixture(directory); f.Scheduler.FailRegistration = true; Throws<IOException>(() => f.Controller.SetAsync(7, default).GetAwaiter().GetResult()); Check(f.Services.Mutations == 0 && f.Store.Plan is null);
        });
        run("A partial service failure rolls back configuration and running state", () => {
            var f = new Fixture(directory); f.Services.FailStopOnce = true; Throws<IOException>(() => f.Controller.SetAsync(7, default).GetAwaiter().GetResult()); Check(f.Services.States.All(s => s.StartType != 4 && s.State == 4) && f.Store.Plan is null && !f.Scheduler.Exists);
        });
        run("Invalid pause durations and unknown services cannot be changed", () => {
            var f = new Fixture(directory); Throws<ArgumentOutOfRangeException>(() => f.Controller.SetAsync(365, default).GetAwaiter().GetResult()); f.Services.States[0] = f.Services.States[0] with { StartType = -1 }; Throws<InvalidOperationException>(() => f.Controller.SetAsync(7, default).GetAwaiter().GetResult()); Check(f.Services.Mutations == 0);
        });
        run("Recovery journal round-trips and rejects unsupported service names", () => {
            var f = new Fixture(directory); var store = new UpdatePlanStore(f.Context.DataDirectory); var plan = new UpdatePlan(Guid.NewGuid(), "Paused", DateTimeOffset.UtcNow.AddDays(7), f.Services.Read()); store.Save(plan); Check(store.Load()!.Token == plan.Token);
            Throws<InvalidDataException>(() => store.Save(plan with { Original = [new("arbitrary-service", 3, 4), new("UsoSvc", 2, 4)] })); store.Clear(); Check(store.Load() is null);
        });
        run("Public key catalog matches desktop editions without a Home fallback", () => {
            Check(ActivationCatalog.Keys.Count == 12 && ActivationCatalog.Match(new("Professional", 26100, "Windows")) == "W269N-WFGWX-YVC9B-4J6C9-T83GX");
            foreach (var id in new[] { "Core", "CoreSingleLanguage", "Unknown", "EnterpriseEval" }) Check(ActivationCatalog.Match(new(id, 26100, "Windows")) is null);
            Check(ActivationCatalog.Match(new("Professional", 26100, "Windows Server", "Server")) is null);
        });
        run("LTSC key mapping respects build generation and IoT support limits", () => {
            Check(ActivationCatalog.Match(new("EnterpriseS", 14393, "Windows")) == "DCPHK-NFMTC-H88MJ-PFHPY-QJ4BJ");
            Check(ActivationCatalog.Match(new("EnterpriseS", 26100, "Windows")) == "M7XTQ-FN8P6-TTKYV-9D4CC-J462D");
            Check(ActivationCatalog.Match(new("IoTEnterpriseS", 19044, "Windows", Revision: 2000)) is null);
        });
        run("Edition detection selects the key but never installs it automatically", () => {
            var f = new Fixture(directory); var page = f.ActivationPage(); Check(page.WindowsWorkspace!.Edition == "Windows 11 Pro" && page.WindowsWorkspace.PublicKey == ActivationCatalog.Keys["Professional"] && f.Activation.Activations == 0);
            f.Activation.Edition = new("Enterprise", 26100, "Windows"); Act(page, page.WindowsWorkspace.RefreshAction!, f.Context); Check(page.WindowsWorkspace.PublicKey == ActivationCatalog.Keys["Enterprise"] && f.Activation.Activations == 0);
        });
        run("Activation refresh reads actual license state independently of the suggested key", () => {
            var f = new Fixture(directory); f.Activation.License = new("Activated", true, "Licensed test product"); var page = f.ActivationPage(); Act(page, page.WindowsWorkspace!.RefreshAction!, f.Context); Check(page.WindowsWorkspace is { IsPositive: true, Badge: "Activated", IsInitialized: true } && f.Activation.Activations == 0);
        });
        run("Unknown editions disable public activation but allow an explicit custom key", () => {
            var f = new Fixture(directory); f.Activation.Edition = new("Core", 26100, "Windows"); var page = f.ActivationPage(); Check(!page.WindowsWorkspace!.ActivateAction!.CanExecute(null) && !page.WindowsWorkspace.HasPublicKey);
            page["custom"].IsChecked = true; page["key"].Value = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"; Check(page.WindowsWorkspace.ActivateAction.CanExecute(null));
        });
        run("Declining activation does not install a key or change licensing", () => {
            var f = new Fixture(directory); f.Context.Approve = false; var page = f.ActivationPage(); Act(page, page.WindowsWorkspace!.ActivateAction!, f.Context); Check(f.Activation.Activations == 0);
        });
        run("Custom activation clears the secret and never persists it", () => {
            var f = new Fixture(directory); var page = f.ActivationPage(); page["custom"].IsChecked = true; page["key"].Value = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"; Act(page, page.WindowsWorkspace!.ActivateAction!, f.Context);
            Check(f.Activation.Activations == 1 && !f.Activation.UsedVolumeKey && page["key"].Value == "" && !f.Context.Settings.Values.Any(v => v.Contains("AAAAA")) && !f.Context.Logs.Any(v => v.Contains("AAAAA")));
        });
        run("Activation backend avoids KMS changes for a custom retail key", () => {
            var f = new Fixture(directory); var backend = new WindowsActivationBackend(f.Context); backend.ActivateAsync("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", "", false, default).GetAwaiter().GetResult(); Check(f.Context.Commands.Count == 2 && !f.Context.Commands.Any(a => a.Contains("/ckms") || a.Contains("/skms")));
        });
        run("Activation input rejects invalid ports and key-install errors redact key output", () => {
            Throws<ArgumentException>(() => WindowsActivationBackend.ValidateInput(ActivationCatalog.Keys["Professional"], "kms.example:99999"));
            var f = new Fixture(directory); f.Context.ExitCode = 1; f.Context.Output = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE";
            try { new WindowsActivationBackend(f.Context).ActivateAsync(f.Context.Output, "", false, default).GetAwaiter().GetResult(); throw new Exception("Expected failure."); } catch (InvalidOperationException e) { Check(!e.Message.Contains("AAAAA")); }
        });
    }
    internal static ModulePage Preview(string directory, bool activation, string state)
    {
        var f = new Fixture(directory);
        if (activation) { if (state == "home") f.Activation.Edition = new("Core", 26100, "Windows"); if (state == "activated") f.Activation.License = new("Activated", true, "Test license"); var page = f.ActivationPage(); Act(page, page.WindowsWorkspace!.RefreshAction!, f.Context); return page; }
        return UpdatePolicyTests.Preview(directory, state);
    }
    private static void Act(ModulePage page, ModuleAction action, FakeContext context)
    {
        action.Execute(null); if (!SpinWait.SpinUntil(() => !context.IsOperationRunning, 10000)) throw new TimeoutException(action.Title); if (page.Status.StartsWith("Failed")) throw new Exception(page.Status + "\n" + context.Report);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Windows control assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Fixture
    {
        internal readonly FakeServices Services = new(); internal readonly FakeScheduler Scheduler; internal readonly FakeStore Store = new(); internal readonly FakeContext Context; internal readonly FakeActivation Activation = new(); internal readonly UpdateController Controller;
        internal Fixture(string directory) { var path = Path.Combine(directory, "windows-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); Context = new(path); Scheduler = new(Services); Controller = new(Services, Scheduler, Store); }
        internal ModulePage ActivationPage() => new ActivationModule(_ => Activation).CreatePage(Context);
    }
    internal sealed class FakeServices : IWindowsUpdateServices
    {
        internal UpdateServiceState[] States = [new("wuauserv", 3, 4), new("UsoSvc", 2, 4)]; internal int Mutations, Checks, ReenableOnStoppedRead; internal bool FailStopOnce, AlwaysReenable; internal readonly List<string> Operations = [];
        public UpdateServiceState[] Read() { if (States.All(s => s.StartType == 4 && s.State == 1) && (AlwaysReenable || ReenableOnStoppedRead > 0 && --ReenableOnStoppedRead == 0)) States[0] = States[0] with { StartType = 3, State = 4 }; return States.ToArray(); }
        public Task SetStartAsync(string name, int type, CancellationToken token) { token.ThrowIfCancellationRequested(); Operations.Add("config:" + name); Mutations++; var i = Array.FindIndex(States, s => s.Name == name); States[i] = States[i] with { StartType = type }; return Task.CompletedTask; }
        public Task StopAsync(string name, CancellationToken token) { Operations.Add("stop:" + name); if (name == "UsoSvc" && FailStopOnce) { FailStopOnce = false; throw new IOException("Simulated stop failure."); } Mutations++; var i = Array.FindIndex(States, s => s.Name == name); States[i] = States[i] with { State = 1 }; return Task.CompletedTask; }
        public Task StartAsync(string name, CancellationToken token) { Mutations++; var i = Array.FindIndex(States, s => s.Name == name); States[i] = States[i] with { State = 4 }; return Task.CompletedTask; }
        public Task<string> CheckHealthAsync(CancellationToken token) { Checks++; return Task.FromResult("Check succeeded · no download requested."); }
    }
    internal sealed class FakeScheduler(FakeServices services) : IUpdateResumeScheduler
    {
        public bool Exists { get; private set; } internal bool FailRegistration, RegisteredBeforeServiceChange;
        public Task RegisterAsync(Guid token, DateTimeOffset due, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (FailRegistration) throw new IOException("Task registration failed."); RegisteredBeforeServiceChange = services.Mutations == 0; Exists = true; return Task.CompletedTask; }
        public void Remove() => Exists = false;
    }
    internal sealed class FakeStore : IUpdatePlanStore { internal UpdatePlan? Plan; public UpdatePlan? Load() => Plan; public void Save(UpdatePlan p) => Plan = p; public void Clear() => Plan = null; }
    private sealed class FakeActivation : IWindowsActivationBackend
    {
        internal WindowsEdition Edition = new("Professional", 26100, "Windows 10 Pro"); internal WindowsLicense License = new("Not activated", false, "Test product"); internal int Activations; internal bool UsedVolumeKey;
        public WindowsEdition ReadEdition() => Edition;
        public Task<WindowsLicense> ReadLicenseAsync(CancellationToken token) => Task.FromResult(License);
        public Task<string> DetailsAsync(CancellationToken token) => Task.FromResult(License.Details);
        public Task<string> ActivateAsync(string key, string server, bool volumeKey, CancellationToken token) { Activations++; UsedVolumeKey = volumeKey; return Task.FromResult("Simulated activation request complete."); }
    }
    internal sealed class FakeContext(string directory) : IModuleContext
    {
        internal readonly Dictionary<string, string> Settings = []; internal readonly List<string> Logs = []; internal readonly List<string[]> Commands = []; internal bool Approve = true, NativeProcesses; internal string Report = "", Output = "Simulated success"; internal string? Stdout; internal int ExitCode; private bool busy;
        public string DataDirectory => directory; public string Get(string key, string fallback = "") => Settings.GetValueOrDefault(key, fallback); public void Save(string key, string value) => Settings[key] = value; public void Log(string module, string message) => Logs.Add(message);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Approve); public Task ShowReportAsync(string title, string content) { Report = content; return Task.CompletedTask; }
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null); public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) { Commands.Add(arguments.ToArray()); return NativeProcesses ? ProcessRunner.RunAsync(executable, arguments, cancellationToken, workingDirectory, environment) : Task.FromResult(new ProcessResult(ExitCode, Output) { StandardOutput = Stdout }); }
        public void Open(string path) { } public bool IsOperationRunning => busy; public CancellationToken OperationToken => CancellationToken.None; public bool TryBeginOperation() { if (busy) return false; busy = true; return true; } public void EndOperation() => busy = false;
    }
}
