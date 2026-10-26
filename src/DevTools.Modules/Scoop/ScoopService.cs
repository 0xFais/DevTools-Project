using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevTools.Contracts;

namespace DevTools.Modules.Scoop;

public sealed class ScoopService
{
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    public static string SafeSegment(string value)
    {
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9_][A-Za-z0-9_.+-]*$") || value.EndsWith('.') ||
            Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use letters, numbers, dots, underscores and hyphens; a normal folder name is required.");
        return value;
    }
    public static string Within(string parent, string child)
    {
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(child);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path must be inside " + parent);
        return full;
    }
    public static string DetectRoot(string saved)
    {
        var candidates = new List<string?> { saved, Environment.GetEnvironmentVariable("SCOOP"), Environment.GetEnvironmentVariable("SCOOP", EnvironmentVariableTarget.User) };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            try
            {
                var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(entry.Trim('"')));
                if (File.Exists(Path.Combine(dir.FullName, "scoop.cmd")) || File.Exists(Path.Combine(dir.FullName, "scoop.ps1")))
                    for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent) candidates.Add(dir.FullName);
            }
            catch (ArgumentException) { }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"));
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "apps", "scoop", "current", "bin", "scoop.ps1"))) ?? "";
    }
    public static JsonObject Read(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
    public static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(Text) : node is null ? [] : [Text(node)];
    public static string ManifestDirectory(string bucket) => Directory.Exists(Path.Combine(bucket, "bucket")) ? Path.Combine(bucket, "bucket") : bucket;
    public static string MetadataPath(string directory, string legacyName)
    {
        var modern = Path.Combine(directory, "scoop-" + legacyName);
        return File.Exists(modern) ? modern : Path.Combine(directory, legacyName);
    }
    public static IEnumerable<string> IconCandidates(string appDirectory)
    {
        var current = Current(appDirectory); var m = Read(MetadataPath(current, "manifest.json"));
        var paths = new List<string>();
        void Add(JsonNode? node)
        {
            if (node is JsonArray array) foreach (var item in array) { if (item is JsonArray tuple) paths.Add(Text(tuple.FirstOrDefault())); else paths.Add(Text(item)); }
            else if (node is not null) paths.Add(Text(node));
        }
        Add(m["shortcuts"]); Add(m["bin"]); Add(m["icon"]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in paths)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string path;
            try { path = Path.GetFullPath(Path.Combine(current, Environment.ExpandEnvironmentVariables(entry.Replace("$dir", current)))); }
            catch (ArgumentException) { continue; }
            if (File.Exists(path) && seen.Add(path)) yield return path;
            if (!Path.HasExtension(path) && File.Exists(path + ".exe") && seen.Add(path + ".exe")) yield return path + ".exe";
        }
        if (!Directory.Exists(current)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 8 };
        foreach (var path in Directory.EnumerateFiles(current, "*", options).Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".ico", StringComparison.OrdinalIgnoreCase)).Take(256).OrderBy(f => Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase)).ThenBy(f => f.Length))
            if (seen.Add(path)) yield return path;
    }
    public static List<ScoopBucket> Buckets(string root) => Directory.Exists(Path.Combine(root, "buckets")) ? Directory.GetDirectories(Path.Combine(root, "buckets")).Select(d => new ScoopBucket(Path.GetFileName(d), Directory.GetFiles(ManifestDirectory(d), "*.json").Length)).OrderBy(b => b.Name).ToList() : [];
    public static string Current(string app)
    {
        var current = new DirectoryInfo(Path.Combine(app, "current"));
        return current.Exists ? current.ResolveLinkTarget(true)?.FullName ?? current.FullName : current.FullName;
    }
    public static bool ScoopHeld(string root)
    {
        var config = Path.Combine(root, "config.json");
        if (!File.Exists(config)) config = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "scoop", "config.json");
        var values = Read(config);
        var until = values.FirstOrDefault(p => p.Key.Equals("HOLD_UPDATE_UNTIL", StringComparison.OrdinalIgnoreCase)).Value;
        return DateTimeOffset.TryParse(Text(until), out var date) && date > DateTimeOffset.Now;
    }
    public static List<ScoopPackage> Installed(string root, CancellationToken ct)
    {
        var list = new List<ScoopPackage>();
        var bucketDirs = Buckets(root).Select(b => (b.Name, Path: ManifestDirectory(Path.Combine(root, "buckets", b.Name)))).ToArray();
        if (!Directory.Exists(Path.Combine(root, "apps"))) return list;
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "apps")))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            var current = Current(dir);
            var manifest = MetadataPath(current, "manifest.json");
            var m = Read(manifest); var install = Read(MetadataPath(current, "install.json"));
            var bucket = Text(install["bucket"]);
            var source = bucketDirs.FirstOrDefault(b => (bucket.Length > 0 || Text(install["url"]).Length == 0) && (bucket.Length == 0 || b.Name == bucket) && File.Exists(Path.Combine(b.Path, name + ".json")));
            if (bucket.Length == 0) bucket = Text(install["url"]).Length > 0 ? "custom/local" : source.Name ?? "custom/local";
            var latest = source.Path is null ? "" : Text(Read(Path.Combine(source.Path, name + ".json"))["version"]);
            var version = Text(m["version"]);
            if (version.Length == 0) version = Directory.Exists(current) ? Path.GetFileName(current) : "(no current)";
            var held = name == "scoop" ? ScoopHeld(root) : Text(install["hold"]).Equals("true", StringComparison.OrdinalIgnoreCase);
            var status = latest.Length > 0 ? (latest == version ? "Up to date" : "Update: " + latest) : "Local / unknown";
            list.Add(new(name, version, bucket, held, status, dir, manifest, Text(m["description"]), Text(m["homepage"]), Text(m["license"] is JsonObject lic ? lic["identifier"] : m["license"])));
        }
        return list.OrderBy(a => a.Name).ToList();
    }
    public static List<ScoopPackage> Search(string root, string query, CancellationToken ct)
    {
        var list = new List<ScoopPackage>();
        foreach (var bucket in Buckets(root))
            foreach (var file in Directory.EnumerateFiles(ManifestDirectory(Path.Combine(root, "buckets", bucket.Name)), "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var m = Read(file);
                list.Add(new(name, Text(m["version"]), bucket.Name, false, Directory.Exists(Path.Combine(root, "apps", name)) ? "Installed" : "Available", "", file, Text(m["description"]), Text(m["homepage"]), Text(m["license"] is JsonObject lic ? lic["identifier"] : m["license"])));
                if (list.Count == 500) return list;
            }
        return list;
    }
    public static string Script(string root, IEnumerable<string> args)
    {
        var scoop = Path.Combine(root, "apps", "scoop", "current", "bin", "scoop.ps1");
        if (!File.Exists(scoop)) throw new DirectoryNotFoundException("Select a Scoop root containing apps\\scoop\\current\\bin\\scoop.ps1.");
        return "$ErrorActionPreference='Stop'; $env:SCOOP=" + Literal(root) + "; & " + Literal(scoop) + " " + string.Join(" ", args.Select(Literal)) + "; if ($LASTEXITCODE) { exit $LASTEXITCODE }";
    }
    public static string[] GitRepositories(string root)
    {
        var candidates = new List<string> { Path.Combine(root, "apps", "scoop", "current") };
        if (Directory.Exists(Path.Combine(root, "buckets"))) candidates.AddRange(Directory.GetDirectories(Path.Combine(root, "buckets")));
        return candidates.Where(path => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
            .SelectMany(path => { var directory = new DirectoryInfo(path); var actual = directory.ResolveLinkTarget(true)?.FullName; return actual is null ? new[] { Path.GetFullPath(path) } : new[] { Path.GetFullPath(path), actual }; })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static bool OwnershipBlocked(string output) => output.Contains("detected dubious ownership", StringComparison.OrdinalIgnoreCase);
    public static string WithGitTrust(string script, IEnumerable<string> repositories)
    {
        var prefix = new StringBuilder("$__devToolsGitCount=0; if ($env:GIT_CONFIG_COUNT) { $__devToolsGitCount=[int]$env:GIT_CONFIG_COUNT }; ");
        foreach (var repository in repositories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(repository) || repository.Contains('*') || repository.Contains('?')) throw new ArgumentException("Git trust requires exact absolute repository paths.");
            prefix.Append("[Environment]::SetEnvironmentVariable('GIT_CONFIG_KEY_'+$__devToolsGitCount,'safe.directory','Process'); ");
            prefix.Append("[Environment]::SetEnvironmentVariable('GIT_CONFIG_VALUE_'+$__devToolsGitCount,").Append(Literal(repository.Replace('\\', '/'))).Append(",'Process'); $__devToolsGitCount++; ");
        }
        prefix.Append("$env:GIT_CONFIG_COUNT=[string]$__devToolsGitCount; ");
        return prefix + script;
    }
    public static async Task RunScript(string script, CancellationToken ct, IProgress<string> log)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // EncodedCommand serializes non-output streams as CLIXML even with OutputFormat Text.
        // Merge PowerShell records before host serialization and emit plain console text.
        var wrapper = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; try { & { " + script + "\n } *>&1 | ForEach-Object { [Console]::Out.WriteLine([string]$_) }; if ($LASTEXITCODE) { exit $LASTEXITCODE } } catch { [Console]::Error.WriteLine($_.ToString()); [Console]::Error.WriteLine($_.ScriptStackTrace); exit 1 }";
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper)) }) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new IOException("PowerShell did not start.");
        async Task Pump(StreamReader reader) { while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) log.Report(line); }
        var output = Pump(process.StandardOutput); var error = Pump(process.StandardError);
        try { await Task.WhenAll(process.WaitForExitAsync(ct), output, error).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0) throw new IOException($"Scoop exited with code {process.ExitCode}. See Activity for details.");
    }
    public static long FolderSize(string path, CancellationToken ct)
    {
        long total = 0;
        foreach (var f in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (f is FileInfo file) total += file.Length;
            else if (f is DirectoryInfo folder) total += FolderSize(folder.FullName, ct);
        }
        return total;
    }
    public static void ValidateTree(string source, CancellationToken ct)
    {
        var d = new DirectoryInfo(source);
        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folders cannot be imported. Choose a regular folder.");
        foreach (var f in d.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Import contains a link: " + f.FullName);
            if (f is DirectoryInfo sub) ValidateTree(sub.FullName, ct);
        }
    }
    public static void CopyTree(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var f in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Import contains a link: " + f.FullName);
            var dest = Within(target, Path.Combine(target, f.Name));
            if (f is DirectoryInfo sub) CopyTree(sub.FullName, dest, ct); else File.Copy(f.FullName, dest);
        }
    }
    public static JsonObject CustomManifest(string version, string description, string relativeExe, string shim, string shortcut) => new()
    {
        ["version"] = version, ["description"] = description, ["homepage"] = "", ["license"] = "Unknown",
        ["bin"] = shim.Length == 0 ? new JsonArray(relativeExe) : new JsonArray(new JsonArray(relativeExe, shim)),
        ["shortcuts"] = new JsonArray(new JsonArray(relativeExe, shortcut))
    };
}
