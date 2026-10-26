using DevTools.Contracts;
using DevTools.Infrastructure;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DevTools.App;

public sealed class AppModuleContext : IModuleContext, IAppDataLocations
{
    private readonly SettingsStore settings;
    private readonly SettingsStore machineSettings;
    private readonly PortableStorage? portable;
    private readonly string? recoveryDataRoot;
    private readonly object logGate = new();
    private CancellationTokenSource? operation;
    public string DataDirectory
    {
        get;
    }
    public event Action<string, string>? Logged;
    public string SettingsDirectory => portable?.DataRoot ?? recoveryDataRoot ?? DataDirectory;
    public string LogsDirectory => portable is null && recoveryDataRoot is null ? DataDirectory : Path.Combine(SettingsDirectory, "Logs");
    public event Action? OperationChanged;
    public event Action? SettingsChanged;
    public bool IsOperationRunning => operation is not null;
    public CancellationToken OperationToken => operation?.Token ?? CancellationToken.None;
    public AppModuleContext(PortableStorage storage)
    {
        portable = storage;
        DataDirectory = storage.MachineDirectory;
        settings = new SettingsStore(storage.DataRoot);
        machineSettings = new SettingsStore(DataDirectory);
    }
    public AppModuleContext(string? directory = null)
    {
        if (directory is null)
        {
            portable = PortableStorage.OpenDefault();
            DataDirectory = portable.MachineDirectory;
            settings = new SettingsStore(portable.DataRoot);
            machineSettings = new SettingsStore(DataDirectory);
        }
        else
        {
            DataDirectory = Path.GetFullPath(directory); settings = new SettingsStore(DataDirectory); machineSettings = settings;
            var profiles = Path.GetDirectoryName(DataDirectory);
            var dataRoot = profiles is null ? null : Path.GetDirectoryName(profiles);
            if (File.Exists(Path.Combine(DataDirectory, "portable-profile.json")) && Path.GetFileName(profiles) == "Machines" && Path.GetFileName(dataRoot) == "Data") recoveryDataRoot = dataRoot;
        }
    }
    public string Get(string key, string fallback = "")
    {
        var value = (portable is null || PortableStorage.SharedSetting(key) ? settings : machineSettings).Get(key, fallback);
        return portable?.DecodePath(key, value) ?? value;
    }
    public void Save(string key, string value)
    {
        (portable is null || PortableStorage.SharedSetting(key) ? settings : machineSettings).Save(key, portable?.EncodePath(key, value) ?? value);
        if (key == "settings.theme")
            SettingsChanged?.Invoke();
    }
    public void Log(string module, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss}  {module}  ·  {message}";
        lock (logGate)
        {
            Directory.CreateDirectory(LogsDirectory);
            if (portable is not null || recoveryDataRoot is not null)
            {
                var previous = Path.Combine(DataDirectory, "activity.log");
                var archive = Path.Combine(LogsDirectory, "previous-" + Path.GetFileName(DataDirectory) + ".log");
                if (File.Exists(previous) && !File.Exists(archive)) File.Copy(previous, archive);
            }
            File.AppendAllText(Path.Combine(LogsDirectory, "activity.log"), line + Environment.NewLine);
        }
        Logged?.Invoke(module, line);
    }
    public Task<bool> ConfirmAsync(string title, string message)
    {
        if (!NoticeWindow.Show(title, message, confirm: true))
            throw new ActionDeclinedException();
        return Task.FromResult(true);
    }
    public Task ShowReportAsync(string title, string content)
    {
        var report = new ReportWindow(title, content) { Owner = ThemedWindow.ActiveOwner };
        report.ShowDialog();
        return Task.CompletedTask;
    }
    public Task<string?> PickFolderAsync(string title)
    {
        var dialog = new ThemedFilePicker(title, foldersOnly: true) { Owner = ThemedWindow.ActiveOwner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.SelectedPath : null);
    }
    public Task<string?> PickFileAsync(string title, string filter)
    {
        var dialog = new ThemedFilePicker(title, foldersOnly: false, filter: filter) { Owner = ThemedWindow.ActiveOwner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.SelectedPath : null);
    }
    public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) => ProcessRunner.RunAsync(executable, arguments, cancellationToken, workingDirectory, environment);
    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public bool TryBeginOperation()
    {
        if (operation is not null)
            return false;
        operation = new();
        OperationChanged?.Invoke();
        return true;
    }
    public void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        OperationChanged?.Invoke();
    }
    public void Cancel() => operation?.Cancel();
}
