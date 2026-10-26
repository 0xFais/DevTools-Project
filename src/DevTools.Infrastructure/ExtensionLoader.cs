using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DevTools.Contracts;

namespace DevTools.Infrastructure;

public sealed record ExtensionManifest(string Id, string Name, string EntryAssembly, int ApiVersion = 1);
public sealed class ExtensionLoader
{
    private readonly List<AssemblyLoadContext> contexts = [];
    public IEnumerable<IDevToolsModule> Load(string directory, Func<string, bool> enabled, Action<string> report)
    {
        Directory.CreateDirectory(directory);
        foreach (var manifestFile in Directory.EnumerateFiles(directory, "extension.json", SearchOption.AllDirectories))
        {
            List<IDevToolsModule> loaded = [];
            try
            {
                var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty extension manifest.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9.-]+$"))
                    throw new InvalidDataException("Invalid extension ID.");
                if (!enabled(manifest.Id))
                {
                    report($"{manifest.Name}: disabled; enable in Settings and restart.");
                    continue;
                }
                if (manifest.ApiVersion != 1)
                    throw new InvalidDataException("Unsupported extension API version.");
                var folder = Path.GetFullPath(Path.GetDirectoryName(manifestFile)!);
                var assemblyPath = Path.GetFullPath(Path.Combine(folder, manifest.EntryAssembly));
                if (!assemblyPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Extension assembly must be inside its extension directory.");
                var context = new PluginContext(assemblyPath);
                contexts.Add(context);
                loaded.AddRange(context.LoadFromAssemblyPath(assemblyPath).GetTypes().Where(t => typeof(IDevToolsModule).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).Select(t => (IDevToolsModule)Activator.CreateInstance(t)!));
                if (loaded.Count == 0)
                    throw new InvalidDataException("No IDevToolsModule implementation found.");
            }
            catch (Exception e) { report(Path.GetFileName(Path.GetDirectoryName(manifestFile)) + ": " + e.Message); }
            foreach (var module in loaded)
                yield return module;
        }
    }
    private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IDevToolsModule).Assembly.GetName().Name)
                return typeof(IDevToolsModule).Assembly;
            var resolved = resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var resolved = resolver.ResolveUnmanagedDllToPath(name);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
