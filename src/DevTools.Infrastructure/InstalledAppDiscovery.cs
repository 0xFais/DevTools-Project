using Microsoft.Win32;

namespace DevTools.Infrastructure;

public interface IInstalledAppDiscovery
{
    IReadOnlyDictionary<string, string> Find(IEnumerable<string> executables, string portableRoot, CancellationToken token);
}

/// <summary>One bounded directory walk per scan, not one walk per catalog entry.</summary>
public sealed class InstalledAppDiscovery : IInstalledAppDiscovery
{
    public IReadOnlyDictionary<string, string> Find(IEnumerable<string> executables, string portableRoot, CancellationToken token)
    {
        var wanted = executables.Where(n => n != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted)
        {
            token.ThrowIfCancellationRequested();
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                var path = (key?.GetValue("") as string)?.Trim('"');
                if (File.Exists(path))
                {
                    result[name] = path!;
                    break;
                }
            }
        }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')));
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            foreach (var name in wanted.Where(n => !result.ContainsKey(n)))
                try
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path))
                        result[name] = path;
                }
                catch (ArgumentException) { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[] { portableRoot == "" ? "" : Path.Combine(portableRoot, "apps"), Path.Combine(local, "Programs"),
            Path.Combine(local, "JetBrains", "Toolbox", "apps"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Walk(root, 5);
        return result;

        void Walk(string folder, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == wanted.Count)
                return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.exe"))
                {
                    var name = Path.GetFileName(file);
                    if (wanted.Contains(name))
                        result.TryAdd(name, file);
                }
                if (depth <= 0)
                    return;
                foreach (var directory in Directory.EnumerateDirectories(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        Walk(directory, depth - 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }
}
