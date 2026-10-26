using DevTools.Contracts;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Development;

public sealed class SdkCatalogModule : IDevToolsModule
{
    public string Id => "sdks-runtimes";

    public ModulePage CreatePage(IModuleContext context)
    {
        var workspace = new SdkCatalogWorkspace();
        var page = new ModulePage(Id, "SDKs & Runtimes", "Inspect installed developer SDKs, runtimes, and host details.", "Developer Tools", "\uE8A5") { SdkCatalogWorkspace = workspace };
        var controller = new SdkCatalogController(workspace, page, context);
        workspace.Commands["Refresh"] = new ReadOnlyRefreshCommand(controller.Refresh);
        controller.Refresh();
        return page;
    }
}

internal sealed class SdkCatalogController(SdkCatalogWorkspace workspace, ModulePage page, IModuleContext context)
{
    public async void Refresh()
    {
        try
        {
            page.Status = "Inspecting installed SDKs and runtimes…";
            var token = context.OperationToken;
            var dotnet = FindExecutable("dotnet.exe") ?? FindDotnetRoot();
            var java = FindExecutable("java.exe");
            var node = FindExecutable("node.exe");
            var python = FindExecutable("python.exe");
            var rust = FindExecutable("rustc.exe");
            var ecosystems = new List<SdkEcosystemInfo>();

            if (dotnet is not null)
            {
                workspace.DotnetPath = dotnet;
                var sdkOutput = await Run(dotnet, "--list-sdks", token);
                var runtimeOutput = await Run(dotnet, "--list-runtimes", token);
                var infoOutput = await Run(dotnet, "--info", token);
                ParseHost(infoOutput, dotnet);
                var sdks = ParseSdks(sdkOutput, workspace.Architecture);
                var runtimes = ParseRuntimes(runtimeOutput);
                Replace(workspace.DotnetSdks, sdks);
                Replace(workspace.DotnetRuntimes, runtimes);
                workspace.SdkDetails = sdks.Count == 0 ? "No SDKs detected." : string.Join(Environment.NewLine, sdks.Select(item => $"{item.Version} · {item.Architecture}"));
                workspace.RuntimeDetails = runtimes.Count == 0 ? "No runtimes detected." : string.Join(Environment.NewLine, runtimes.Select(item => $"{item.Name} {item.Version}"));
                var versions = sdks.Select(item => item.Version).ToArray();
                ecosystems.Add(new(".NET", versions.Length == 0 ? "No SDKs installed" : string.Join(", ", versions), dotnet, $"{sdks.Count} SDKs · {runtimes.Count} runtimes"));
            }
            else
            {
                workspace.DotnetPath = "Not found on process PATH or DOTNET_ROOT";
                workspace.HostVersion = "Not detected";
                workspace.Architecture = "—";
                workspace.Rid = "—";
                workspace.HostSummary = "Host not detected";
                workspace.BasePath = "—";
                Replace(workspace.DotnetSdks, []);
                Replace(workspace.DotnetRuntimes, []);
                workspace.SdkDetails = "No SDKs detected.";
                workspace.RuntimeDetails = "No runtimes detected.";
                ecosystems.Add(new(".NET", "Not detected", "", "dotnet.exe was not found on process PATH or DOTNET_ROOT."));
            }

            ecosystems.Add(await DetectSimple("Java", java, "-version", token, "JVM and Java development tools"));
            ecosystems.Add(await DetectSimple("Node.js", node, "--version", token, "JavaScript runtime and V8 engine"));
            ecosystems.Add(await DetectSimple("Python", python, "--version", token, "Python interpreter"));
            ecosystems.Add(await DetectSimple("Rust", rust, "--version", token, "Rust compiler and toolchain"));
            workspace.Ecosystems.Clear();
            foreach (var ecosystem in ecosystems) workspace.Ecosystems.Add(ecosystem);
            workspace.SelectedEcosystem = workspace.Ecosystems.FirstOrDefault(item => item.Name == ".NET");
            workspace.Summary = $"{ecosystems.Count(item => item.Location.Length > 0)} of {ecosystems.Count} ecosystems detected · {workspace.DotnetSdks.Count} .NET SDKs · {workspace.DotnetRuntimes.Count} .NET runtimes";
            page.Status = "SDK and runtime inventory refreshed";
            context.Log("SDKs & Runtimes", workspace.Summary);
        }
        catch (Exception error)
        {
            page.Status = "Failed · " + error.Message;
            context.Log("SDKs & Runtimes", error.Message);
        }
    }

    private async Task<SdkEcosystemInfo> DetectSimple(string name, string? executable, string arguments, CancellationToken token, string description)
    {
        if (executable is null) return new(name, "Not detected", "", description);
        var output = await Run(executable, arguments, token);
        var version = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Installed";
        return new(name, version, executable, description);
    }

    private async Task<string> Run(string executable, string arguments, CancellationToken token)
    {
        try
        {
            var result = await context.RunAsync(executable, [arguments], token);
            return result.Output;
        }
        catch (Exception error) { return "Could not inspect this installation: " + error.Message; }
    }

    private void ParseHost(string info, string dotnet)
    {
        var host = Regex.Match(info, "(?m)^Host:\\s*\\r?\\n\\s*Version:\\s*(?<version>[^\\r\\n]+)\\r?\\n\\s*Architecture:\\s*(?<architecture>[^\\r\\n]+)");
        var rid = Regex.Match(info, "(?m)^\\s*RID:\\s*(?<value>[^\\r\\n]+)");
        var basePath = Regex.Match(info, "(?m)^\\s*Base Path:\\s*(?<value>[^\\r\\n]+)");
        workspace.HostVersion = host.Success ? host.Groups["version"].Value.Trim() : "Not detected";
        workspace.Architecture = host.Success ? host.Groups["architecture"].Value.Trim() : "—";
        workspace.Rid = rid.Success ? rid.Groups["value"].Value.Trim() : "—";
        workspace.BasePath = basePath.Success ? basePath.Groups["value"].Value.Trim() : "—";
        workspace.HostSummary = $"Host {workspace.HostVersion} · {workspace.Architecture} · {workspace.Rid}";
        if (workspace.BasePath == "—") workspace.BasePath = Path.GetDirectoryName(dotnet) ?? "—";
    }

    private static List<InstalledSdkInfo> ParseSdks(string output, string architecture)
    {
        var result = new List<InstalledSdkInfo>();
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, "^(?<version>\\S+)\\s+\\[(?<path>.+)\\]$");
            if (match.Success) result.Add(new(match.Groups["version"].Value, architecture, Path.Combine(match.Groups["path"].Value, match.Groups["version"].Value)));
        }
        return result;
    }

    private static List<InstalledRuntimeInfo> ParseRuntimes(string output)
    {
        var result = new List<InstalledRuntimeInfo>();
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, "^(?<name>\\S+)\\s+(?<version>\\S+)\\s+\\[(?<path>.+)\\]$");
            if (match.Success) result.Add(new(match.Groups["name"].Value, match.Groups["version"].Value, Path.Combine(match.Groups["path"].Value, match.Groups["version"].Value)));
        }
        return result;
    }

    private static string? FindExecutable(string name)
    {
        var extensions = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [name] : new[] { name, name + ".exe", name + ".cmd", name + ".bat" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = Environment.ExpandEnvironmentVariables(directory.Trim('"'));
            foreach (var extension in extensions)
            {
                var path = Path.Combine(expanded, extension);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static string? FindDotnetRoot()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "dotnet.exe");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static void Replace<T>(System.Collections.ObjectModel.ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }
}
