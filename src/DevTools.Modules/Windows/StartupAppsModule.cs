using DevTools.Contracts;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed record StartupCandidate(string Name, string Source, string Scope, string Command, string Status, StartupEntryIdentity Identity);
public sealed record StartupReadResult(IReadOnlyList<StartupCandidate> Items, IReadOnlyList<string> Errors);
public sealed record StartupRemovalResult(string ArchivePath, bool WasStartupFile);

public interface IStartupAppsBackend
{
    StartupReadResult Read();
    string AddCurrentUser(string name, string executable, string arguments);
    StartupRemovalResult Remove(StartupEntryIdentity identity, string dataDirectory);
}

public static class StartupAppsService
{
    public static bool MatchesRegistryValue(StartupEntryIdentity identity, StartupHive hive, StartupView view, string valueName, StartupRegistryKind kind, string rawValueJson) =>
        identity.Type == StartupEntryType.RegistryValue && identity.Hive == hive && identity.View == view &&
        string.Equals(identity.ValueName, valueName, StringComparison.OrdinalIgnoreCase) && identity.RegistryKind == kind &&
        string.Equals(identity.RawValueJson, rawValueJson, StringComparison.Ordinal);

    public static StartupEntryIdentity CaptureFileIdentity(string path, string startupRoot)
    {
        var fullRoot = Path.GetFullPath(startupRoot);
        var fullPath = Path.GetFullPath(path);
        EnsureDirectStartupFile(fullPath, fullRoot);
        var info = new FileInfo(fullPath);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            throw new IOException("Only regular files directly inside the Startup folder can be removed.");
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var scope = !string.IsNullOrWhiteSpace(common) && fullRoot.Equals(Path.GetFullPath(common), StringComparison.OrdinalIgnoreCase) ? StartupScope.AllUsers : StartupScope.CurrentUser;
        return new(StartupEntryType.StartupFile, Path: fullPath, StartupRoot: fullRoot, Scope: scope, Length: info.Length,
            LastWriteUtcTicks: info.LastWriteTimeUtc.Ticks, Sha256: HashFile(fullPath));
    }

    public static bool MatchesFile(StartupEntryIdentity identity)
    {
        try
        {
            if (identity.Type != StartupEntryType.StartupFile || identity.Path is null || identity.StartupRoot is null || identity.Sha256 is null) return false;
            var current = CaptureFileIdentity(identity.Path, identity.StartupRoot);
            return current.Length == identity.Length && current.LastWriteUtcTicks == identity.LastWriteUtcTicks &&
                   string.Equals(current.Sha256, identity.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static StartupRemovalResult ArchiveStartupFile(StartupEntryIdentity identity, string dataDirectory)
    {
        if (identity.Path is null) throw new InvalidDataException("The Startup folder identity has no file path.");
        if (!MatchesFile(identity)) throw new IOException("The Startup folder entry changed or is no longer present; it was left untouched. Refresh and inspect it before retrying.");
        var path = identity.Path!;
        var archiveRoot = CreateSafeArchiveRoot(dataDirectory);
        var directory = Path.Combine(archiveRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        EnsureNoReparsePath(directory, Path.GetFullPath(dataDirectory));
        var archivePath = Path.Combine(directory, Path.GetFileName(path));
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var sourceInfo = new FileInfo(path);
        var sourceHash = Convert.ToHexString(SHA256.HashData(source));
        if (sourceInfo.Length != identity.Length || sourceInfo.LastWriteTimeUtc.Ticks != identity.LastWriteUtcTicks || !sourceHash.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Startup file changed while it was being opened; it was left untouched.");
        try
        {
            File.Move(path, archivePath, overwrite: false);
            var archivedInfo = new FileInfo(archivePath);
            var archivedAttributes = File.GetAttributes(archivePath);
            var archivedHash = (archivedAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 ? HashFile(archivePath) : "";
            if ((archivedAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || archivedInfo.Length != identity.Length || !archivedHash.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase) || File.Exists(path))
            {
                try { File.Move(archivePath, path, overwrite: false); } catch { }
                throw new IOException("The archived Startup file failed verification; any recoverable copy was kept at: " + archivePath);
            }
            return new(archivePath, true);
        }
        catch when (!File.Exists(path) && File.Exists(archivePath))
        {
            try { File.Move(archivePath, path, overwrite: false); } catch { }
            throw;
        }
    }

    internal static string CreateSafeArchiveRoot(string dataDirectory)
    {
        // The configured data directory may live in a redirected profile or portable
        // junction. Trust that location, but reject redirection inside our archive.
        var dataRoot = Path.GetFullPath(dataDirectory);
        var full = Path.Combine(dataRoot, "StartupApps", "Removed");
        EnsureNoReparsePath(full, dataRoot);
        Directory.CreateDirectory(full);
        EnsureNoReparsePath(full, dataRoot);
        return full;
    }

    public static string RegistryBackupJson(StartupEntryIdentity identity)
    {
        if (identity.Type != StartupEntryType.RegistryValue || identity.Hive is null || identity.View is null || identity.ValueName is null || identity.RegistryKind is null || identity.RawValueJson is null)
            throw new InvalidDataException("The selected registry startup entry has incomplete identity data.");
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Serialize(new { Hive = identity.Hive, View = identity.View, Key = @"Software\Microsoft\Windows\CurrentVersion\Run", identity.ValueName, RegistryKind = identity.RegistryKind, RawValue = identity.RawValueJson }, options);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureDirectStartupFile(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root) ||
            !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Path.GetFileName(path)) || !File.Exists(path))
            throw new IOException("The selected item is not a file directly inside its Startup folder.");
        EnsureNoReparsePath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Startup folder is a reparse point; the selected entry was left untouched.");
    }

    private static void EnsureNoReparsePath(string path, string? trustedRoot = null)
    {
        var current = trustedRoot ?? Path.GetPathRoot(path) ?? throw new IOException("The archive path has no root.");
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The startup path includes a reparse point at '{current}'; it was not used.");
        }
    }

    public static string ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Choose an executable using its full path.");
        var full = Path.GetFullPath(executable);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Choose an existing .exe file.");
        if (full.Contains('"')) throw new ArgumentException("The executable path contains an unsupported quote character.");
        return full;
    }

    public static string BuildCommand(string executable, string arguments)
    {
        var full = ValidateExecutable(executable);
        if (arguments is null || arguments.Any(char.IsControl)) throw new ArgumentException("Command-line arguments contain invalid control characters.");
        var args = arguments.Trim();
        var command = "\"" + full + "\"" + (args.Length == 0 ? "" : " " + args);
        if (command.Any(char.IsControl)) throw new ArgumentException("The command contains a control character.");
        if (command.Length > 260) throw new ArgumentException("The startup command exceeds the Windows Run command limit of 260 characters.");
        return command;
    }
}

public sealed class StartupAppsModule(IStartupAppsBackend? backend = null) : IDevToolsModule
{
    public string Id => "startup-apps";
    public ModulePage CreatePage(IModuleContext context)
    {
        var system = backend ?? new WindowsStartupAppsBackend();
        var workspace = new StartupAppsWorkspace();
        var page = new ModulePage(Id, "Startup apps", "See apps configured to start with Windows and add an app for your account.", "Windows", "\uE7E8") { StartupAppsWorkspace = workspace };
        void Add(string title, Func<CancellationToken, Task> execute, bool primary = false) => page.Action(title, execute, context, primary);
        async Task Refresh(CancellationToken ct)
        {
            var snapshot = await Task.Run(system.Read, ct);
            var items = snapshot.Items;
            var errors = snapshot.Errors;
            workspace.SelectedItem = null;
            workspace.Items.Clear();
            foreach (var item in items) workspace.Items.Add(new StartupAppItem(item.Name, item.Source, item.Scope, item.Command, item.Status, item.Identity));
            workspace.IsInitialized = true;
            workspace.Summary = $"{items.Count} startup entries · registry Run keys and Startup folders" + (errors.Count == 0 ? "" : $" · {errors.Count} source(s) could not be read");
            workspace.Result = string.Join("\n", errors);
        }
        Add("Refresh startup apps", Refresh); workspace.RefreshAction = page.Actions[^1];
        Add("Choose .exe", async ct =>
        {
            if (await context.PickFileAsync("Choose an application to start with Windows", "Executable files (*.exe)|*.exe") is { } path)
            {
                StartupAppsService.ValidateExecutable(path);
                workspace.AppName = Path.GetFileNameWithoutExtension(path);
                workspace.SelectedExecutable = path;
                workspace.Result = "Selected " + path + ". It will not be run.";
            }
        }); workspace.BrowseAction = page.Actions[^1];
        Add("Add to my startup", async ct =>
        {
            var path = workspace.SelectedExecutable;
            var name = workspace.AppName.Trim();
            if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Enter a startup name of 1–120 characters without filename punctuation.");
            var command = StartupAppsService.BuildCommand(path, workspace.Arguments);
            if (!await context.ConfirmAsync("Add application to startup?", $"Add {name} to the current user's Windows startup entries?\n\n{command}\n\nThe app will not be run now.")) throw new ActionDeclinedException();
            var valueName = await Task.Run(() => system.AddCurrentUser(name, path, workspace.Arguments), ct);
            await Refresh(CancellationToken.None);
            var readWarnings = workspace.Result.Length == 0 ? "" : "\n" + workspace.Result;
            workspace.Result = $"Added {valueName} for the current user and verified the saved command. The selected app was not run." + readWarnings;
        }, true); workspace.AddAction = page.Actions[^1];
        Add("Remove from startup", async ct =>
        {
            var item = workspace.SelectedItem ?? throw new InvalidOperationException("Select a startup entry first.");
            ct.ThrowIfCancellationRequested();
            var allUsers = item.Identity.Hive == StartupHive.LocalMachine || item.Identity.Scope == StartupScope.AllUsers;
            var impact = allUsers ? "This is an all-users startup entry." : "This is a current-user startup entry.";
            var handling = item.Identity.Type == StartupEntryType.StartupFile
                ? Path.GetExtension(item.Identity.Path ?? "").Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(item.Identity.Path ?? "").Equals(".url", StringComparison.OrdinalIgnoreCase)
                    ? $"The shortcut itself will be archived under {Path.Combine(context.DataDirectory, "StartupApps", "Removed")}; its target application will not be moved or deleted."
                    : $"The selected startup file itself will be moved into the archive under {Path.Combine(context.DataDirectory, "StartupApps", "Removed")}. It will not be deleted; direct executable or script files move with it."
                : $"The original registry value will be backed up under {Path.Combine(context.DataDirectory, "StartupApps", "Removed")} before the entry is removed. StartupApproved state and the target application will be kept.";
            var runtimeNote = item.Identity.Type == StartupEntryType.StartupFile && !Path.GetExtension(item.Identity.Path ?? "").Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(item.Identity.Path ?? "").Equals(".url", StringComparison.OrdinalIgnoreCase)
                ? "No process will be stopped or launched."
                : "The target app remains installed and any running instance keeps running.";
            if (!await context.ConfirmAsync("Remove startup entry?", $"{item.Name}\n{item.Source} · {item.Scope}\n{impact}\n\n{handling}\n\n{runtimeNote}")) throw new ActionDeclinedException();
            ct.ThrowIfCancellationRequested();
            StartupRemovalResult removed;
            try { removed = await Task.Run(() => system.Remove(item.Identity, context.DataDirectory), CancellationToken.None); }
            catch { await Refresh(CancellationToken.None); throw; }
            await Refresh(CancellationToken.None);
            var warnings = workspace.Result.Length == 0 ? "" : "\n" + workspace.Result;
            workspace.SelectedItem = null;
            workspace.Result = removed.WasStartupFile
                ? $"Removed the startup file. It was archived, not deleted. Archived at: {removed.ArchivePath}{warnings}"
                : $"Removed the registry startup entry. The app remains installed and any running instance keeps running. Original value backed up at: {removed.ArchivePath}{warnings}";
        }); workspace.RemoveAction = page.Actions[^1]; workspace.RemoveAction.IsEnabled = false;
        Add("Open Windows startup settings", ct => { context.Open("ms-settings:startupapps"); return Task.CompletedTask; }); workspace.SettingsAction = page.Actions[^1];
        return page;
    }

}

public sealed class WindowsStartupAppsBackend : IStartupAppsBackend
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private static readonly object WriteLock = new();

    public StartupReadResult Read()
    {
        var result = new List<StartupCandidate>();
        var errors = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var viewLabel = view == RegistryView.Registry64 ? "64-bit" : "32-bit";
            if (view == RegistryView.Registry64) ReadRun(result, errors, RegistryHive.CurrentUser, view, RunKey, "HKCU Run", "Current user", viewLabel);
            ReadRun(result, errors, RegistryHive.LocalMachine, view, RunKey, "HKLM Run", "All users", viewLabel);
        }
        ReadStartupFolder(result, errors, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup folder", "Current user");
        ReadStartupFolder(result, errors, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup folder", "All users");
        return new(result, errors);
    }

    private static void ReadRun(List<StartupCandidate> output, List<string> errors, RegistryHive hive, RegistryView view, string path, string source, string scope, string viewLabel)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var command = raw?.ToString() ?? "";
                    var (approved, approvalError) = ReadApproval(hive, ApprovalView(), hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 ? "Run32" : "Run", name);
                    var identity = new StartupEntryIdentity(StartupEntryType.RegistryValue, ToStartupHive(hive), ToStartupView(view), name, ToStartupKind(kind), JsonSerializer.Serialize(raw), Scope: hive == RegistryHive.LocalMachine ? StartupScope.AllUsers : StartupScope.CurrentUser);
                    output.Add(new(name, source + " (" + viewLabel + ")", scope, command, approved, identity));
                    if (approvalError is not null) errors.Add($"{source} ({viewLabel}) approval for {name}: {approvalError}");
                }
                catch (Exception e) { errors.Add($"{source} ({viewLabel}) entry {name}: {e.Message}"); }
            }
        }
        catch (Exception e) { errors.Add($"{source} ({viewLabel}): {e.Message}"); }
    }

    private static (string Status, string? Error) ReadApproval(RegistryHive hive, RegistryView view, string subkey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(Approved + subkey);
            if (key?.GetValue(name) is not byte[] bytes) return ("Unknown (no StartupApproved value)", null);
            return (ParseApproval(bytes), null);
        }
        catch (Exception e) { return ("Unknown (StartupApproved unreadable)", e.Message); }
    }

    public static string ParseApproval(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 12) return "Unknown (malformed StartupApproved value)";
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) switch
        {
            0x03 => "Disabled (StartupApproved)",
            0x02 => "Enabled (StartupApproved)",
            _ => "Unknown (StartupApproved value)"
        };
    }

    private static RegistryView ApprovalView() => Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

    private static void ReadStartupFolder(List<StartupCandidate> output, List<string> errors, string folder, string source, string scope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var (status, error) = ReadApproval(source == "Common Startup folder" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, ApprovalView(), "StartupFolder", name);
                    var identity = StartupAppsService.CaptureFileIdentity(file, folder);
                    output.Add(new(name, source, scope, "\"" + file + "\"", status, identity));
                    if (error is not null) errors.Add($"{source} approval for {name}: {error}");
                }
                catch (Exception e) { errors.Add($"{source} entry {Path.GetFileName(file)}: {e.Message}"); }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) { errors.Add(source + ": " + e.Message); }
    }

    public string AddCurrentUser(string name, string executable, string arguments)
    {
        var command = StartupAppsService.BuildCommand(executable, arguments);
        lock (WriteLock)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new UnauthorizedAccessException("Could not open the current-user Run key for writing.");
            var approvedRun = Registry.CurrentUser.OpenSubKey(Approved + "Run");
            var approvedRun32 = Registry.CurrentUser.OpenSubKey(Approved + "Run32");
            try
            {
                var names = key.GetValueNames().Concat(approvedRun?.GetValueNames() ?? []).Concat(approvedRun32?.GetValueNames() ?? []);
                var unique = ChooseUniqueName(name, names);
                VerifyWrite(unique, command, candidate => key.SetValue(candidate, command, RegistryValueKind.String), candidate => key.GetValue(candidate, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString());
                return unique;
            }
            finally { approvedRun?.Dispose(); approvedRun32?.Dispose(); }
        }
    }

    public StartupRemovalResult Remove(StartupEntryIdentity identity, string dataDirectory) => identity.Type switch
    {
        StartupEntryType.RegistryValue => RemoveRegistryValue(identity, dataDirectory),
        StartupEntryType.StartupFile => RemoveStartupFile(identity, dataDirectory),
        _ => throw new InvalidDataException("Unknown startup entry identity.")
    };

    private static StartupRemovalResult RemoveStartupFile(StartupEntryIdentity identity, string dataDirectory)
    {
        if (identity.StartupRoot is null) throw new InvalidDataException("The Startup folder identity is missing its root.");
        var permitted = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath);
        if (!permitted.Contains(Path.GetFullPath(identity.StartupRoot), StringComparer.OrdinalIgnoreCase))
            throw new IOException("The selected file is outside the known current-user and common Startup folders; it was left untouched.");
        return StartupAppsService.ArchiveStartupFile(identity, dataDirectory);
    }

    private static StartupRemovalResult RemoveRegistryValue(StartupEntryIdentity identity, string dataDirectory)
    {
        _ = StartupAppsService.RegistryBackupJson(identity);
        var entryHive = identity.Hive ?? throw new InvalidDataException("The startup entry hive is missing.");
        var entryView = identity.View ?? throw new InvalidDataException("The startup entry registry view is missing.");
        var valueName = identity.ValueName ?? throw new InvalidDataException("The startup entry value name is missing.");
        var hive = ToRegistryHive(entryHive);
        var view = ToRegistryView(entryView);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(RunKey, writable: true) ?? throw new IOException("The startup Run key is unavailable; the entry was left untouched.");
        if (!TryReadRaw(key, valueName, out var kind, out var rawJson) || !StartupAppsService.MatchesRegistryValue(identity, entryHive, entryView, valueName, kind, rawJson))
            throw new IOException("The startup registry value changed or is no longer present; it was left untouched. Refresh and inspect it before retrying.");
        var archiveRoot = StartupAppsService.CreateSafeArchiveRoot(dataDirectory);
        var archivePath = Path.Combine(archiveRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        var backup = StartupAppsService.RegistryBackupJson(identity);
        using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) { writer.Write(backup); writer.Flush(); stream.Flush(true); }
        if (!TryReadRaw(key, valueName, out kind, out rawJson) || !StartupAppsService.MatchesRegistryValue(identity, entryHive, entryView, valueName, kind, rawJson))
            throw new IOException("The startup registry value changed while its backup was written; it was left untouched. Backup: " + archivePath);
        key.DeleteValue(valueName, throwOnMissingValue: false);
        if (key.GetValueNames().Any(name => name.Equals(valueName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The startup registry entry is still present. Its original value backup is at: " + archivePath);
        return new(archivePath, false);
    }

    private static bool TryReadRaw(RegistryKey key, string name, out StartupRegistryKind kind, out string rawJson)
    {
        try
        {
            kind = ToStartupKind(key.GetValueKind(name));
            rawJson = JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            return true;
        }
        catch (IOException) { kind = StartupRegistryKind.Unknown; rawJson = ""; return false; }
    }

    private static StartupHive ToStartupHive(RegistryHive hive) => hive == RegistryHive.CurrentUser ? StartupHive.CurrentUser : StartupHive.LocalMachine;
    private static RegistryHive ToRegistryHive(StartupHive hive) => hive switch { StartupHive.CurrentUser => RegistryHive.CurrentUser, StartupHive.LocalMachine => RegistryHive.LocalMachine, _ => throw new InvalidDataException("Unknown startup registry hive.") };
    private static StartupView ToStartupView(RegistryView view) => view switch { RegistryView.Registry32 => StartupView.Registry32, RegistryView.Registry64 => StartupView.Registry64, _ => throw new InvalidDataException("Unknown startup registry view.") };
    private static RegistryView ToRegistryView(StartupView view) => view switch { StartupView.Registry32 => RegistryView.Registry32, StartupView.Registry64 => RegistryView.Registry64, _ => throw new InvalidDataException("Unknown startup registry view.") };
    private static StartupRegistryKind ToStartupKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => StartupRegistryKind.String,
        RegistryValueKind.ExpandString => StartupRegistryKind.ExpandString,
        RegistryValueKind.Binary => StartupRegistryKind.Binary,
        RegistryValueKind.DWord => StartupRegistryKind.DWord,
        RegistryValueKind.MultiString => StartupRegistryKind.MultiString,
        RegistryValueKind.QWord => StartupRegistryKind.QWord,
        RegistryValueKind.None => StartupRegistryKind.None,
        _ => StartupRegistryKind.Unknown
    };

    public static string ChooseUniqueName(string name, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var suffix = 2; names.Contains(candidate); suffix++) candidate = name + " (" + suffix + ")";
        return candidate;
    }

    public static void VerifyWrite(string name, string command, Action<string> write, Func<string, string?> read)
    {
        write(name);
        string? value;
        try { value = read(name); }
        catch (Exception error)
        {
            throw new IOException($"Startup entry '{name}' was written but could not be read back. Refresh and inspect it in Windows startup settings before retrying.", error);
        }
        if (string.Equals(value, command, StringComparison.Ordinal)) return;
        throw new IOException($"Startup entry '{name}' changed during verification. Its current value was preserved. Refresh and inspect it before retrying.");
    }
}
