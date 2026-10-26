using System.Security.Principal;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DevTools.Tests")]

namespace DevTools.Modules.Windows;

public sealed class UpdateResumeScheduler(string dataDirectory) : IUpdateResumeScheduler
{
    private static string TaskName => "DevToolsStudio-ResumeUpdates-" + WindowsIdentity.GetCurrent().User!.Value;
    private static dynamic Connect() { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable."))!; service.Connect(); return service; }
    internal static bool TryTaskOperation(Action operation)
    {
        try { operation(); return true; }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { return false; }
    }
    public bool Exists
    {
        get { dynamic service = Connect(); dynamic folder = service.GetFolder("\\"); return TryTaskOperation(() => { _ = folder.GetTask(TaskName); }); }
    }
    public Task RegisterAsync(Guid token, DateTimeOffset resumeAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var helper = PrepareHelper(AppContext.BaseDirectory, dataDirectory);
        dynamic service = Connect(); dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "Restore previous Windows Update settings at the DevTools deadline. Runs while this user is signed in or at the next sign-in.";
        task.Principal.UserId = WindowsIdentity.GetCurrent().User!.Value; task.Principal.LogonType = 3; task.Principal.RunLevel = 1;
        task.Settings.Enabled = true; task.Settings.StartWhenAvailable = true; task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false; task.Settings.ExecutionTimeLimit = "PT10M";
        task.Settings.RestartCount = 3; task.Settings.RestartInterval = "PT1M";
        dynamic time = task.Triggers.Create(1); time.StartBoundary = resumeAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"); time.Enabled = true;
        dynamic logon = task.Triggers.Create(9); logon.UserId = WindowsIdentity.GetCurrent().User!.Value; logon.Enabled = true;
        dynamic action = task.Actions.Create(0); action.Path = Path.Combine(helper, "DevTools.exe"); action.Arguments = RecoveryArguments(token, dataDirectory); action.WorkingDirectory = helper;
        dynamic folder = service.GetFolder("\\"); folder.RegisterTaskDefinition(TaskName, task, 6, WindowsIdentity.GetCurrent().User!.Value, null, 3);
        return Task.CompletedTask;
    }
    internal static string RecoveryArguments(Guid token, string directory) => "--resume-updates " + token.ToString("D") + " --data-directory \"" + Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + "\"";
    internal static string PrepareHelper(string appDirectory, string directory)
    {
        var helper = Path.Combine(Path.GetFullPath(directory), "UpdateRecovery");
        Directory.CreateDirectory(helper);
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)).Equals(Path.TrimEndingDirectorySeparator(helper), StringComparison.OrdinalIgnoreCase)) return helper;
        foreach (var name in new[] { "DevTools.exe", "DevTools.dll", "DevTools.runtimeconfig.json", "DevTools.deps.json", "DevTools.Contracts.dll", "DevTools.Infrastructure.dll", "DevTools.Modules.dll" })
            if (!File.Exists(Path.Combine(appDirectory, name))) throw new FileNotFoundException("Recovery helper component missing.", name);
        // Include self-contained CoreCLR, WPF and native runtime files; seven managed files alone cannot run portably.
        foreach (var source in Directory.EnumerateFiles(appDirectory))
            if (Path.GetExtension(source).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".dat") File.Copy(source, Path.Combine(helper, Path.GetFileName(source)), true);
        foreach (var source in Directory.EnumerateDirectories(appDirectory))
        {
            var name = Path.GetFileName(source);
            var runtimeFolder = name == "runtimes";
            if (!runtimeFolder) try { _ = System.Globalization.CultureInfo.GetCultureInfo(name); runtimeFolder = name.Contains('-') || name.Length is 2 or 3; } catch (System.Globalization.CultureNotFoundException) { }
            if (runtimeFolder) CopyFolder(source, Path.Combine(helper, name));
        }
        return helper;
    }
    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var folder in Directory.EnumerateDirectories(source))
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
    public async Task RebindPortableLocationAsync(CancellationToken ct)
    {
        var candidates = new List<(Guid Token, DateTimeOffset Due)>();
        if (new UpdatePauseStore(dataDirectory).Load() is { } pause) candidates.Add((pause.Token, pause.RestoreAt));
        if (new UpdatePolicyStore(dataDirectory).Load() is { RestoreAt: { } policyDue } policy) candidates.Add((policy.Token, policyDue));
        if (new UpdatePlanStore(dataDirectory).Load() is { ResumeAt: { } serviceDue } plan) candidates.Add((plan.Token, serviceDue));
        if (candidates.Count == 0) return;
        var location = Path.Combine(dataDirectory, "recovery-location.txt");
        var current = Path.GetFullPath(dataDirectory);
        if (File.Exists(location) && File.ReadAllText(location).Equals(current, StringComparison.OrdinalIgnoreCase) && Exists) return;
        var next = candidates.OrderBy(p => p.Due).First();
        await RegisterAsync(next.Token, next.Due, ct);
        File.WriteAllText(location, current);
    }
    public void Remove()
    {
        if (!Exists) return;
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        TryTaskOperation(() => folder.DeleteTask(TaskName, 0));
    }
}
