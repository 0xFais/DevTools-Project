using System.Collections;
using System.Windows.Input;
using DevTools.Contracts;

namespace DevTools.Modules.Development;

public sealed class EnvironmentPathModule : IDevToolsModule
{
    public string Id => "environment-path";

    public ModulePage CreatePage(IModuleContext context)
    {
        var workspace = new EnvironmentPathWorkspace();
        var page = new ModulePage(Id, "Environment & PATH", "Inspect environment variable scopes, PATH order, and command resolution.", "Developer Tools", "\uE8A5") { EnvironmentPathWorkspace = workspace };
        var controller = new EnvironmentPathController(workspace, page, context);
        workspace.FilterChanged += controller.Filter;
        workspace.QueryChanged += controller.ResolveExecutable;
        workspace.Commands["Refresh"] = new ReadOnlyRefreshCommand(controller.Refresh);
        controller.Refresh();
        return page;
    }
}

internal sealed class EnvironmentPathController(EnvironmentPathWorkspace workspace, ModulePage page, IModuleContext context)
{
    public void Refresh()
    {
        try
        {
            var process = ReadVariables(EnvironmentVariableTarget.Process);
            var user = ReadVariables(EnvironmentVariableTarget.User);
            var machine = ReadVariables(EnvironmentVariableTarget.Machine);
            var names = process.Keys.Concat(user.Keys).Concat(machine.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            var variables = names.Select(name =>
            {
                var processValue = process.GetValueOrDefault(name, "");
                var userValue = user.GetValueOrDefault(name, "");
                var machineValue = machine.GetValueOrDefault(name, "");
                var source = processValue.Length == 0 ? "Not set" : userValue.Length > 0 && machineValue.Length > 0 ? "Machine + User" : userValue.Length > 0 ? "User" : machineValue.Length > 0 ? "Machine" : "Process";
                if (IsSensitive(name))
                {
                    processValue = Mask(processValue);
                    userValue = Mask(userValue);
                    machineValue = Mask(machineValue);
                }
                return new EnvironmentVariableInfo(name, processValue, userValue, machineValue, source);
            }).ToArray();

            workspace.Variables.Clear();
            foreach (var variable in variables) workspace.Variables.Add(variable);
            workspace.PathEntries.Clear();
            var machinePaths = Split(machine.GetValueOrDefault("PATH", ""));
            var userPaths = Split(user.GetValueOrDefault("PATH", ""));
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entries = Split(process.GetValueOrDefault("PATH", ""));
            for (var i = 0; i < entries.Length; i++)
            {
                var expanded = Expand(entries[i]);
                var key = Normalize(expanded);
                var duplicate = key.Length > 0 && seen.TryGetValue(key, out var first) ? first.ToString() : "";
                if (key.Length > 0 && duplicate.Length == 0) seen[key] = i + 1;
                var inMachine = ContainsPath(machinePaths, expanded);
                var inUser = ContainsPath(userPaths, expanded);
                var scope = inMachine && inUser ? "Machine + User" : inMachine ? "Machine" : inUser ? "User" : "Process";
                var exists = expanded.Length > 0 && Directory.Exists(expanded);
                workspace.PathEntries.Add(new PathEntryInfo(i + 1, entries[i], scope, exists ? "Available" : "Folder missing", duplicate));
            }
            workspace.SelectedVariable = workspace.Variables.FirstOrDefault(item => item.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? workspace.Variables.FirstOrDefault();
            workspace.Summary = $"{workspace.Variables.Count} process variables · {workspace.PathEntries.Count} PATH entries · {workspace.PathEntries.Count(item => item.DuplicateOf.Length > 0)} duplicates";
            Filter();
            ResolveExecutable();
            page.Status = "Environment and PATH inspected";
            context.Log("Environment & PATH", workspace.Summary);
        }
        catch (Exception error)
        {
            page.Status = "Failed · " + error.Message;
            context.Log("Environment & PATH", error.Message);
        }
    }

    public void Filter()
    {
        var rows = workspace.Variables.Where(item => string.IsNullOrWhiteSpace(workspace.Search) || item.Name.Contains(workspace.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        workspace.VisibleVariables.Clear();
        foreach (var row in rows) workspace.VisibleVariables.Add(row);
    }

    public void ResolveExecutable()
    {
        var query = workspace.ExecutableQuery.Trim().Trim('"');
        if (query.Length == 0) { workspace.ResolvedExecutable = "Enter a command name to search process PATH."; return; }
        var match = workspace.PathEntries.Select(entry => Expand(entry.Path)).Where(Directory.Exists).Select(directory => Path.Combine(directory, query)).FirstOrDefault(File.Exists);
        workspace.ResolvedExecutable = match ?? $"No match for {query} in process PATH.";
    }

    private static Dictionary<string, string> ReadVariables(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariables(target).Cast<DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
        }
        catch (PlatformNotSupportedException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static string[] Split(string value) => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => item.Trim('"')).Where(item => item.Length > 0).ToArray();
    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
    private static string Normalize(string value)
    {
        if (value.Length == 0) return "";
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
    private static bool ContainsPath(IEnumerable<string> candidates, string path)
    {
        var normalized = Normalize(Expand(path));
        return normalized.Length > 0 && candidates.Any(candidate => Normalize(Expand(candidate)).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsSensitive(string name) => new[] { "PASSWORD", "TOKEN", "SECRET", "CREDENTIAL", "PRIVATE_KEY", "ACCESS_KEY" }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    private static string Mask(string value) => value.Length == 0 ? "" : "••••••";
}

internal sealed class ReadOnlyRefreshCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
