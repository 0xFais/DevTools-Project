using System.Net.Http;
using DevTools.Contracts;
using DevTools.Infrastructure;

namespace DevTools.Modules.Scoop;

public interface IScoopSetupBackend
{
    string UserPath { get; set; }
    string ProcessPath { get; set; }
    void Broadcast();
    Task DownloadInstaller(string destination, CancellationToken ct);
    Task Run(string script, CancellationToken ct, IProgress<string> log);
}
public sealed class WindowsScoopSetupBackend : IScoopSetupBackend
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public string UserPath { get => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? ""; set => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User); }
    public string ProcessPath { get => Environment.GetEnvironmentVariable("PATH") ?? ""; set => Environment.SetEnvironmentVariable("PATH", value); }
    public void Broadcast() => WindowsEnvironment.Broadcast();
    public async Task DownloadInstaller(string destination, CancellationToken ct)
    {
        using var response = await http.GetAsync(ScoopSetupService.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        var buffer = new byte[81920]; long length = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            length += count; if (length > 5 * 1024 * 1024) throw new InvalidDataException("Installer download is unexpectedly large.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (length == 0) throw new InvalidDataException("Installer download was empty.");
    }
    public Task Run(string script, CancellationToken ct, IProgress<string> log) => ScoopService.RunScript(script, ct, log);
}
public sealed record ScoopSetupResult(ScoopSetupState State, string Root, string Message);
public sealed class ScoopSetupService(IScoopSetupBackend backend)
{
    public const string InstallerUrl = "https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1";
    public static bool ContainsPath(string path, string entry)
    {
        static string Normalize(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\', '/');
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(part => Normalize(part).Equals(Normalize(entry), StringComparison.OrdinalIgnoreCase));
    }
    public static string AppendPath(string path, string entry) => ContainsPath(path, entry) ? path : path.Length == 0 ? entry : path.TrimEnd(';') + ";" + entry;
    public ScoopSetupResult Probe(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return new(ScoopSetupState.Missing, "", "Install Scoop or locate an existing installation.");
        root = Path.GetFullPath(root);
        var entry = Path.Combine(root, "apps", "scoop", "current", "bin", "scoop.ps1");
        if (!File.Exists(entry))
        {
            var partial = Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
            return new(partial ? ScoopSetupState.Broken : ScoopSetupState.Missing, root, partial ? "The selected folder does not contain a working Scoop installation. Choose another folder. Existing files will be kept." : "Scoop was not found. Install it or locate an existing installation.");
        }
        var shims = Path.Combine(root, "shims");
        if (!File.Exists(Path.Combine(shims, "scoop.cmd")) && !File.Exists(Path.Combine(shims, "scoop.ps1"))) return new(ScoopSetupState.Broken, root, "Scoop's launcher is missing from its shims folder. Repair Scoop or choose another installation.");
        var onPath = ContainsPath(backend.UserPath, shims);
        return new(onPath ? ScoopSetupState.Ready : ScoopSetupState.PathMissing, root, onPath ? "Installed at " + root + " · Available in your user PATH" : "Add " + shims + " to your user PATH. Existing entries will be preserved.");
    }
    public async Task AddToPath(string root, Action<string> backup, CancellationToken ct, IProgress<string> log)
    {
        var status = Probe(root);
        if (status.State is not (ScoopSetupState.Ready or ScoopSetupState.PathMissing)) throw new InvalidOperationException(status.Message);
        var shims = Path.Combine(status.Root, "shims"); var before = backend.UserPath; var after = AppendPath(before, shims);
        ct.ThrowIfCancellationRequested();
        if (after != before) { backup(before); backend.UserPath = after; }
        if (!ContainsPath(backend.UserPath, shims)) throw new IOException("The user PATH change could not be verified.");
        backend.ProcessPath = AppendPath(backend.ProcessPath, shims);
        if (after != before) backend.Broadcast();
        log.Report("User PATH verified. Open a new terminal to use Scoop.");
        await backend.Run(ScoopService.Script(status.Root, ["--version"]), ct, log);
    }
    public static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination) || destination.Contains(';')) throw new ArgumentException("Choose a full local install path without semicolons.");
        var root = Path.GetFullPath(destination).TrimEnd('\\');
        if (root.StartsWith("\\\\") || root.Equals(Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an app folder on a local drive, not the drive root or a network folder.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose an installation folder without links in its path.");
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("The install folder is not empty. Locate an existing Scoop installation or choose a new folder.");
        return root;
    }
    public static string InstallScript(string installer, string root) => "$ErrorActionPreference='Stop'; $env:SCOOP=" + ScoopService.Literal(root) + "; & " + ScoopService.Literal(installer) + " -ScoopDir " + ScoopService.Literal(root) + " -RunAsAdmin; if ($LASTEXITCODE) { exit $LASTEXITCODE }";
    public async Task Install(string destination, string dataDirectory, Action<string> backup, CancellationToken ct, IProgress<string> log)
    {
        var root = ValidateDestination(destination); Directory.CreateDirectory(dataDirectory);
        var installer = Path.Combine(dataDirectory, "scoop-install-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            log.Report("Downloading the official Scoop installer from " + InstallerUrl);
            await backend.DownloadInstaller(installer, ct);
            _ = ValidateDestination(root); ct.ThrowIfCancellationRequested();
            backup(backend.UserPath);
            log.Report("Installing Scoop to " + root + "…");
            await backend.Run(InstallScript(installer, root), ct, log);
            var status = Probe(root);
            if (status.State is not (ScoopSetupState.Ready or ScoopSetupState.PathMissing)) throw new IOException("Installation did not produce a working Scoop launcher. " + status.Message);
            await AddToPath(root, backup, ct, log);
            log.Report("Scoop installation and PATH verification completed.");
        }
        finally { if (File.Exists(installer)) File.Delete(installer); }
    }
}
