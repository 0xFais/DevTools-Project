using System.Collections.ObjectModel;
using System.Diagnostics;
using DevTools.Contracts;
using DevTools.Infrastructure;
using DevTools.Modules.Scoop;

namespace DevTools.Modules.Development;

public sealed class BinaryInventoryModule : IDevToolsModule
{
    public string Id => "binaries";

    public ModulePage CreatePage(IModuleContext context)
    {
        var workspace = new BinaryInventoryWorkspace();
        var page = new ModulePage(Id, "Binaries", "Scoop installed developer tools, their versions, and package locations.", "Developer Tools", "\uE8A5") { BinaryInventoryWorkspace = workspace };
        foreach (var item in Definitions()) workspace.Items.Add(item);
        var controller = new BinaryInventoryController(workspace, page, context);
        workspace.FilterChanged += controller.Filter;
        workspace.Commands["Scan"] = new InventoryCommand(controller.Scan, controller.Error);
        controller.Filter();
        controller.Scan();
        return page;
    }

    private static IEnumerable<DeveloperBinary> Definitions() =>
    [
        new("Git", "git.exe", "git", "Version control", "Distributed version control for source code."),
        new("Python", "python.exe", "python", "Languages", "Python interpreter and command line tools."),
        new("Node.js", "node.exe", "nodejs", "Languages", "JavaScript runtime for development tools and applications."),
        new(".NET SDK", "dotnet.exe", "dotnet-sdk", "Languages", "Installed .NET SDKs and runtimes supplied by the Scoop dotnet-sdk package."),
        new("CMake", "cmake.exe", "cmake", "Build tools", "Cross platform build configuration."),
        new("Ninja", "ninja.exe", "ninja", "Build tools", "Small and fast build system."),
        new("ripgrep", "rg.exe", "ripgrep", "Utilities", "Fast text search across source trees."),
        new("jq", "jq.exe", "jq", "Utilities", "Command line JSON processor."),
        new("GCC", "gcc.exe", "gcc", "Compilers", "GNU C compiler."),
        new("G++", "g++.exe", "gcc", "Compilers", "GNU C++ compiler."),
        new("GFortran", "gfortran.exe", "gcc", "Compilers", "GNU Fortran compiler."),
        new("MinGW Make", "mingw32-make.exe", "mingw", "Build tools", "GNU Make bundled with MinGW."),
        new("GDB", "gdb.exe", "mingw", "Debuggers", "GNU debugger for native applications."),
        new("MSVC C/C++", "cl.exe", "portable-msvc", "Compilers", "Microsoft C/C++ compiler from the installed Scoop toolchain."),
        new("MSVC Linker", "link.exe", "portable-msvc", "Compilers", "Microsoft linker from the installed Scoop toolchain."),
        new("MSVC Librarian", "lib.exe", "portable-msvc", "Compilers", "Microsoft library manager from the installed Scoop toolchain."),
        new("Clang", "clang.exe", "llvm", "Compilers", "LLVM C compiler."),
        new("Clang++", "clang++.exe", "llvm", "Compilers", "LLVM C++ compiler."),
        new("LLD", "lld.exe", "llvm", "Linkers", "LLVM linker."),
        new("LLDB", "lldb.exe", "llvm", "Debuggers", "LLVM debugger."),
        new("Go", "go.exe", "go", "Languages", "Go compiler and toolchain."),
        new("Rust compiler", "rustc.exe", "rust", "Languages", "Rust compiler."),
        new("Cargo", "cargo.exe", "rust", "Languages", "Rust package manager and build tool."),
        new("Deno", "deno.exe", "deno", "Languages", "JavaScript and TypeScript runtime."),
        new("Bun", "bun.exe", "bun", "Languages", "JavaScript runtime and package manager."),
        new("Java", "java.exe", "temurin-jdk", "Languages", "Java runtime."),
        new("Java compiler", "javac.exe", "temurin-jdk", "Languages", "Java compiler."),
        new("Ruby", "ruby.exe", "ruby", "Languages", "Ruby interpreter."),
        new("PHP", "php.exe", "php", "Languages", "PHP runtime."),
        new("Perl", "perl.exe", "strawberry-perl", "Languages", "Perl interpreter."),
        new("Lua", "lua.exe", "lua", "Languages", "Lua interpreter."),
        new("Zig", "zig.exe", "zig", "Languages", "Zig compiler and build tool."),
        new("NuGet", "NuGet.exe", "nuget", "Package managers", ".NET package manager command line."),
        new("VSwhere", "vswhere.exe", "vswhere", "Build tools", "Find Visual Studio and Build Tools installations."),
        new("MSBuild", "MSBuild.exe", "visualstudio2022buildtools", "Build tools", "Microsoft build engine from Visual Studio Build Tools."),
        new("GNU Make", "make.exe", "make", "Build tools", "GNU Make build automation."),
        new("Meson", "meson.exe", "meson", "Build tools", "Fast build system."),
        new("Gradle", "gradle.bat", "gradle", "Build tools", "Build automation for JVM projects."),
        new("Maven", "mvn.cmd", "maven", "Build tools", "Build and dependency management for Java."),
        new("Bazel", "bazel.exe", "bazel", "Build tools", "Build system for large and multi-language projects."),
        new("Vcpkg", "vcpkg.exe", "vcpkg", "Package managers", "C and C++ library package manager."),
        new("Conan", "conan.exe", "conan", "Package managers", "C and C++ package manager."),
        new("GitHub CLI", "gh.exe", "gh", "Version control", "GitHub command line client."),
        new("Git LFS", "git-lfs.exe", "git-lfs", "Version control", "Git extension for large files."),
        new("Subversion", "svn.exe", "subversion", "Version control", "Apache Subversion client."),
        new("Mercurial", "hg.exe", "mercurial", "Version control", "Distributed source control system."),
        new("Fossil", "fossil.exe", "fossil", "Version control", "Distributed version control and project tracking."),
        new("Docker", "docker.exe", "docker", "Containers", "Docker command line client."),
        new("Podman", "podman.exe", "podman", "Containers", "Container engine command line."),
        new("Kubernetes CLI", "kubectl.exe", "kubectl", "Containers", "Kubernetes cluster command line."),
        new("Helm", "helm.exe", "helm", "Containers", "Kubernetes package manager."),
        new("Terraform", "terraform.exe", "terraform", "Cloud tools", "Infrastructure as code."),
        new("OpenTofu", "tofu.exe", "opentofu", "Cloud tools", "Open source infrastructure as code."),
        new("AWS CLI", "aws.exe", "aws", "Cloud tools", "Amazon Web Services command line."),
        new("Azure CLI", "az.cmd", "azure-cli", "Cloud tools", "Microsoft Azure command line."),
        new("Google Cloud CLI", "gcloud.cmd", "gcloud", "Cloud tools", "Google Cloud command line."),
        new("Pulumi", "pulumi.exe", "pulumi", "Cloud tools", "Infrastructure as code SDK and CLI."),
        new("Packer", "packer.exe", "packer", "Cloud tools", "Machine image builder."),
        new("PostgreSQL client", "psql.exe", "postgresql", "Databases", "PostgreSQL command line client."),
        new("SQLite", "sqlite3.exe", "sqlite", "Databases", "SQLite command line shell."),
        new("MySQL client", "mysql.exe", "mysql", "Databases", "MySQL command line client."),
        new("MongoDB Shell", "mongosh.exe", "mongosh", "Databases", "MongoDB interactive shell."),
        new("Redis CLI", "redis-cli.exe", "redis", "Databases", "Redis command line client."),
        new("DuckDB", "duckdb.exe", "duckdb", "Databases", "In-process analytical database CLI."),
        new("fd", "fd.exe", "fd", "Utilities", "Fast file finder."),
        new("fzf", "fzf.exe", "fzf", "Utilities", "Fuzzy finder for the command line."),
        new("bat", "bat.exe", "bat", "Utilities", "Syntax-highlighted file viewer."),
        new("yq", "yq.exe", "yq", "Utilities", "YAML, JSON, XML and TOML processor."),
        new("Zoxide", "zoxide.exe", "zoxide", "Utilities", "Smarter directory navigation."),
        new("OpenSSL", "openssl.exe", "openssl", "Security tools", "TLS and cryptography command line."),
        new("GnuPG", "gpg.exe", "gnupg", "Security tools", "OpenPGP encryption and signing."),
        new("Protobuf compiler", "protoc.exe", "protobuf", "API tools", "Protocol Buffers compiler."),
        new("Buf", "buf.exe", "buf", "API tools", "Protocol Buffers linting and generation."),
        new("gRPCurl", "grpcurl.exe", "grpcurl", "API tools", "Command line client for gRPC servers."),
        new("k6", "k6.exe", "k6", "Testing tools", "Load testing tool for APIs and services."),
        new("Newman", "newman.cmd", "newman", "Testing tools", "Postman collection runner for command line testing."),
        new("OpenAPI Generator", "openapi-generator-cli.bat", "openapi-generator", "API tools", "Generate API clients and server stubs from OpenAPI."),
        new("VS Code CLI", "code.cmd", "vscode", "Developer tools", "Visual Studio Code command line.")
    ];
}

internal sealed class InventoryCommand(Action execute, Action<Exception> error) : System.Windows.Input.ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { try { execute(); } catch (Exception e) { error(e); } }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

internal sealed class BinaryInventoryController(BinaryInventoryWorkspace workspace, ModulePage page, IModuleContext context)
{
    public void Filter()
    {
        var installed = workspace.Items.Where(item => item.Status.StartsWith("Installed", StringComparison.Ordinal)).ToArray();
        var rows = installed.Where(item =>
            (string.IsNullOrWhiteSpace(workspace.Search) || item.Name.Contains(workspace.Search, StringComparison.OrdinalIgnoreCase) || item.Executable.Contains(workspace.Search, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(workspace.Search, StringComparison.OrdinalIgnoreCase))).ToArray();
        workspace.VisibleItems.Clear();
        foreach (var row in rows) workspace.VisibleItems.Add(row);
        workspace.Summary = $"{installed.Length} developer binaries in Scoop packages";
    }

    public async void Scan()
    {
        try
        {
            page.Status = "Checking developer binaries…";
            await RefreshInventory(context.OperationToken);
            page.Status = "Scoop binary inventory refreshed";
        }
        catch (Exception e) { Error(e); }
    }

    private async Task RefreshInventory(CancellationToken ct)
    {
        var scoopRoot = ScoopRoot();
        var installedPackages = scoopRoot.Length > 0 && Directory.Exists(Path.Combine(scoopRoot, "apps"))
            ? await Task.Run(() => ScoopService.Installed(scoopRoot, ct), ct)
            : new List<ScoopPackage>();
        foreach (var item in workspace.Items)
        {
            ct.ThrowIfCancellationRequested();
            var matchingPackages = item.ScoopPackage == "temurin-jdk"
                ? installedPackages.Where(IsTemurinPackage).OrderByDescending(TemurinMajor).ToList()
                : installedPackages.Where(a => a.Name.Equals(item.ScoopPackage, StringComparison.OrdinalIgnoreCase)).ToList();
            item.Path = ""; item.Version = ""; item.Details = ""; item.Source = "Scoop";
            if (matchingPackages.Count == 0)
            {
                item.Status = "Not installed through Scoop";
                continue;
            }
            var installed = matchingPackages[0];
            var temurinVersions = matchingPackages.Select(package => TemurinMajor(package).ToString()).ToArray();
            item.Status = item.ScoopPackage == "temurin-jdk" && matchingPackages.Count > 1
                ? $"Installed · {matchingPackages.Count} versions"
                : "Installed · " + installed.Version;
            item.Version = item.ScoopPackage == "temurin-jdk" && matchingPackages.Count > 1
                ? string.Join(", ", temurinVersions)
                : installed.Version;
            var current = ScoopService.Current(installed.Directory);
            item.Path = FindPackageExecutable(current, item.Executable) ?? Path.Combine(current, item.Executable);
            if (!File.Exists(item.Path)) item.Details = "Package is installed through Scoop, but its command was not found in the current package folder.";
            if (item.ScoopPackage == "dotnet-sdk")
                await ReadDotnetDetails(item, current, ct);
            else if (item.ScoopPackage == "temurin-jdk")
                item.Details = $"Temurin JDKs installed through Scoop: {string.Join(", ", temurinVersions)}. Using {installed.Name} for this command.";
            else if (item.Details.Length == 0) item.Details = installed.Bucket + " bucket · " + installed.Status;
        }
        var root = ScoopRoot();
        workspace.ScoopMessage = root.Length == 0 ? "Scoop is not set up. Set it up in Apps Manager to install binaries." : "Showing Scoop installed packages only. Other copies on PATH are intentionally excluded.";
        Filter();
        context.Log("Binaries", workspace.Summary);
    }

    private string ScoopRoot() => ScoopService.DetectRoot(context.Get("scoop.root"));

    private static bool IsTemurinPackage(ScoopPackage package) => TemurinMajor(package) > 0;

    private static int TemurinMajor(ScoopPackage package)
    {
        const string prefix = "temurin";
        const string suffix = "-jdk";
        if (!package.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !package.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return 0;
        var version = package.Name[prefix.Length..^suffix.Length];
        return int.TryParse(version, out var major) ? major : 0;
    }

    private static string? FindPackageExecutable(string directory, string executable)
    {
        if (!Directory.Exists(directory)) return null;
        var direct = Path.Combine(directory, executable);
        if (File.Exists(direct)) return direct;
        try
        {
            var matches = Directory.EnumerateFiles(directory, executable, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 10 });
            if (executable.Equals("cl.exe", StringComparison.OrdinalIgnoreCase) || executable.Equals("link.exe", StringComparison.OrdinalIgnoreCase) || executable.Equals("lib.exe", StringComparison.OrdinalIgnoreCase))
                return matches.OrderBy(path => path.Contains(@"\Hostx64\x64\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
            return matches.FirstOrDefault();
        }
        catch (IOException) { return null; }
    }

    private static async Task ReadDotnetDetails(DeveloperBinary item, string packageDirectory, CancellationToken ct)
    {
        var executable = FindPackageExecutable(packageDirectory, "dotnet.exe") ?? "";
        if (!File.Exists(executable)) { item.Details = "Scoop package is installed, but dotnet.exe was not found in its current folder or Scoop shims."; return; }
        item.Path = executable;
        var infoTask = RunDotnet(executable, "--info", ct);
        var sdkTask = RunDotnet(executable, "--list-sdks", ct);
        var runtimeTask = RunDotnet(executable, "--list-runtimes", ct);
        await Task.WhenAll(infoTask, sdkTask, runtimeTask);
        var sdks = sdkTask.Result; var runtimes = runtimeTask.Result; var info = infoTask.Result;
        item.Details = $"Scoop package: {item.Version}\nSDKs ({sdks.Length}):\n{(sdks.Length == 0 ? "None installed" : string.Join("\n", sdks))}\n\nRuntimes ({runtimes.Length}):\n{(runtimes.Length == 0 ? "None installed" : string.Join("\n", runtimes))}\n\n.NET info:\n{string.Join("\n", info.Where(line => line.StartsWith(" RID:", StringComparison.OrdinalIgnoreCase) || line.StartsWith(" Base Path:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("  Version:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("  Architecture:", StringComparison.OrdinalIgnoreCase)))}";
    }

    private static async Task<string[]> RunDotnet(string executable, string arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info);
            if (process is null) return ["Could not start dotnet."];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) { return [$"Could not list runtimes/SDKs: {e.Message}"]; }
    }

    public void Error(Exception error)
    {
        page.Status = "Failed · " + error.Message;
        context.Log("Binaries", error.Message);
    }
}
