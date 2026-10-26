using DevTools.Contracts;
using DevTools.Infrastructure;
using DevTools.Modules.Integrations;
using System.Collections.Concurrent;
using System.IO;

namespace DevTools.Tests;

internal static class IntegrationTests
{
    internal static void RunAll(string artifacts, Action<string, Action> run)
    {
        run("Context search ignores case/extra spaces and preserves matching inspector edits", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            var browser = page.Browser!;
            browser.Selected = browser.Items.Single(i => i.Id == "rider");
            page["files"].IsChecked = false;
            browser.SearchText = "  JETBRAINS   riD  ";
            Check(browser.Items.Count == 1 && browser.Selected!.Id == "rider" && !page["files"].IsChecked);
            browser.SearchText = "folders";
            Check(browser.IsEmpty && f.Registry.Writes == 0);
        });
        run("Link search matches app names and protocols across Added and Available", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            Act(page, "Scan installed apps", f.Context);
            var browser = page.Browser!;
            browser.SearchText = "MAGNET:";
            Check(browser.Items.Count == 2 && browser.Items.Any(i => i.IsAdded) && browser.Items.Any(i => !i.IsAdded));
            browser.SearchText = "free magnet";
            Check(browser.Items.Single().Id == "fdm");
            Check(f.Registry.Writes == 0 && browser.Summary.StartsWith("1 of "));
        });
        run("No-match search clears stale details; clearing restores the full discovered list", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            Act(page, "Scan installed apps", f.Context);
            var browser = page.Browser!;
            var count = browser.Items.Count;
            browser.SearchText = "no-such-app";
            Check(browser.IsEmpty && !browser.HasInspector && browser.Selected is null && browser.EmptyTitle == "No matching applications");
            browser.ClearSearchCommand.Execute(null);
            Check(browser.SearchText == "" && browser.Items.Count == count && browser.HasInspector && !browser.HasSearch);
            Check(f.Registry.Writes == 0);
        });
        run("Scan and refresh retain the query and include newly discovered matches", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            var browser = page.Browser!;
            browser.SearchText = "cursor";
            Check(browser.IsEmpty);
            Act(page, "Scan installed apps", f.Context);
            Check(browser.SearchText == "cursor" && browser.Items.Single().Id == "cursor");
            Act(page, "Refresh added", f.Context);
            Check(browser.SearchText == "cursor" && browser.Selected!.Id == "cursor" && f.Registry.Writes == 0);
        });
        run("Filtering preserves a custom-profile draft", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            Act(page, "Add custom", f.Context);
            page["name"].Value = "Unsaved editor";
            page.Browser!.SearchText = "rider";
            Check(page.Browser.IsCustomEditor && page.Browser.Selected is null && page["name"].Value == "Unsaved editor");
            page.Browser.ClearSearchCommand.Execute(null);
            Check(page.Browser.IsCustomEditor && page["name"].Value == "Unsaved editor" && f.Registry.Writes == 0);
        });
        run("Context list auto-loads registrations without a discovery scan", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            Check(page.Browser!.Items.Any(i => i.Id == "rider" && i.IsAdded));
            Check(page.Browser.Items.All(i => i.IsAdded));
            Check(f.Discovery.Calls == 0 && f.Registry.Writes == 0);
            Check(page["path"].Value == page.Browser.Selected!.ExecutablePath);
        });
        run("Scan adds only installed context options and writes no registry", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            Act(page, "Scan installed apps", f.Context);
            Check(page.Browser!.Items.Any(i => i.Id == "cursor" && !i.IsAdded));
            Check(!page.Browser.Items.Any(i => i.Id == "notepadpp"));
            Check(f.Discovery.Calls == 1 && f.Registry.Writes == 0 && page.Browser.HasScanned);
        });
        run("Inspector loads actual scopes; saving removes unchecked locations", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            page.Browser!.Selected = page.Browser.Items.Single(i => i.Id == "rider");
            Check(page["files"].IsChecked && page["folders"].IsChecked && !page["background"].IsChecked);
            page["folders"].IsChecked = false;
            page["background"].IsChecked = true;
            Act(page, "Save changes", f.Context);
            Check(f.Registry.Read(IntegrationHive.User, ContextMenuModule.Scopes["folders"] + @"Rider\command") is null);
            Check(f.Registry.Read(IntegrationHive.User, ContextMenuModule.Scopes["background"] + @"Rider\command")!.Contains("%V"));
            Check(f.Registry.Read(IntegrationHive.User, ContextMenuModule.Scopes["files"] + @"Unrelated\command") == f.UnrelatedCommand);
        });
        run("Removing a selected context entry leaves other apps untouched", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            page.Browser!.Selected = page.Browser.Items.Single(i => i.Id == "rider");
            Act(page, "Remove from menu", f.Context);
            Check(!page.Browser.Items.Any(i => i.Id == "rider"));
            Check(f.Registry.Read(IntegrationHive.User, ContextMenuModule.Scopes["files"] + @"Unrelated\command") == f.UnrelatedCommand);
        });
        run("Broken context registrations remain visible for repair", () =>
        {
            var f = new Fixture(artifacts);
            f.Registry.Seed(IntegrationHive.User, ContextMenuModule.Scopes["files"] + @"VSCode\command", "", "\"Z:\\missing\\Code.exe\" \"%1\"");
            var item = f.ContextPage().Browser!.Items.Single(i => i.Id == "code");
            Check(item.IsAdded && !item.ExecutableExists && item.Status.Contains("missing"));
        });
        run("External registrations are auto-detected with read-only details", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            var external = page.Browser!.Items.Single(i => i.Id.StartsWith("external:"));
            Check(external.IsAdded && !external.CanEdit);
            page.Browser.Selected = external;
            Check(!page.Browser.CanEdit);
        });
        run("Link auto-detection matches the handler executable, not just scheme", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            Check(page.Browser!.Items.Any(i => i.Id == "qbittorrent" && i.IsAdded));
            Check(!page.Browser.Items.Any(i => i.Id == "fdm"));
            Check(f.Discovery.Calls == 0 && f.Registry.Writes == 0);
        });
        run("Link scan populates available installed options without registering", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            Act(page, "Scan installed apps", f.Context);
            Check(page.Browser!.Items.Any(i => i.Id == "fdm" && !i.IsAdded));
            Check(f.Registry.Writes == 0);
        });
        run("Windows UserChoice resolves and opens Settings without registry writes", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            page.Browser!.Selected = page.Browser.Items.Single(i => i.Id == "vscode");
            Check(page.Browser.Selected.IsAdded && page["scheme"].IsReadOnly);
            Act(page, "Change Windows default", f.Context);
            Check(f.Context.Opened == "ms-settings:defaultapps" && f.Registry.Writes == 0);
        });
        run("Protocol restoration checks ownership and preserves later changes", () =>
        {
            var f = new Fixture(artifacts);
            var service = new LinkRegistrations(f.Registry);
            var original = service.UserCommand("magnet");
            service.Register("magnet", f.Files["fdm.exe"], f.Context);
            Check(service.UserCommand("magnet") != original);
            service.Restore("magnet", f.Context);
            Check(service.UserCommand("magnet") == original);
            service.Register("magnet", f.Files["fdm.exe"], f.Context);
            f.Registry.Seed(IntegrationHive.User, LinkRegistrations.Classes + @"magnet\shell\open\command", "", "\"third-party.exe\" \"%1\"");
            Throws<InvalidOperationException>(() => service.Restore("magnet", f.Context));
            Check(service.UserCommand("magnet").Contains("third-party"));
        });
        run("Custom context profiles are created without automatic registration", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.ContextPage();
            Act(page, "Add custom", f.Context);
            page["name"].Value = "My editor";
            page["path"].Value = f.Files["Cursor.exe"];
            Act(page, "Create custom profile", f.Context);
            Check(page.Browser!.Selected is { IsAdded: false, IsCustom: true } && f.Registry.Writes == 0);
            Check(File.Exists(Path.Combine(f.Context.DataDirectory, "context-custom.json")));
        });
        run("Custom protocol profiles persist and register only on explicit action", () =>
        {
            var f = new Fixture(artifacts);
            var page = f.LinkPage();
            Act(page, "Add custom", f.Context);
            page["customName"].Value = "My handler";
            page["path"].Value = f.Files["Cursor.exe"];
            page["scheme"].Value = "my-app";
            Act(page, "Create custom profile", f.Context);
            Check(f.Registry.Writes == 0 && page.Browser!.Selected!.IsCustom);
            Act(page, "Register handler", f.Context);
            Check(page.Browser!.Selected!.IsAdded);
            var reopened = f.LinkPage();
            Check(reopened.Browser!.Items.Any(i => i.Name == "My handler" && i.IsAdded));
        });
        run("List refresh preserves selection and counts", () =>
        {
            var browser = new IntegrationBrowser();
            var a = new IntegrationItem("a", "Alpha", "", true, false, "files");
            var b = new IntegrationItem("b", "Beta", "", false, true, "folders");
            browser.ReplaceItems([a, b]);
            browser.Selected = b;
            browser.ReplaceItems([a, b with { Locations = "background" }]);
            Check(browser.Selected!.Id == "b" && browser.Summary == "1 added · 1 available");
            browser.ReplaceItems([]);
            Check(browser.IsEmpty && !browser.HasInspector);
        });
    }

    internal static ModulePage Preview(string artifacts, bool links)
    {
        var f = new Fixture(artifacts);
        var page = links ? f.LinkPage() : f.ContextPage();
        Act(page, "Scan installed apps", f.Context);
        page.Browser!.Selected = page.Browser.Items.First(i => i.Id == (links ? "qbittorrent" : "rider"));
        return page;
    }
    private static void Act(ModulePage page, string title, TestContext context)
    {
        page.Actions.Single(a => a.Title == title).Execute(null);
        if (!SpinWait.SpinUntil(() => !context.IsOperationRunning, 10000))
            throw new TimeoutException(title);
        if (page.Status.StartsWith("Failed"))
            throw new InvalidOperationException(page.Status + "\n" + context.Report);
    }
    private static void Check(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Integration assertion failed.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture
    {
        internal readonly MemoryRegistry Registry = new();
        internal readonly TestContext Context;
        internal readonly FakeDiscovery Discovery;
        internal readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        internal string UnrelatedCommand => '"' + Files["unrelated.exe"] + "\" \"%1\"";
        internal Fixture(string artifacts)
        {
            var folder = Path.Combine(artifacts, "integration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Context = new(folder);
            foreach (var name in new[] { "rider64.exe", "Cursor.exe", "qbittorrent.exe", "fdm.exe", "Code.exe", "unrelated.exe" })
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, "test fixture only; not executable");
                Files[name] = path;
            }
            Discovery = new(Files.Where(p => p.Key is "Cursor.exe" or "fdm.exe").ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
            foreach (var scope in new[] { "files", "folders" })
                Registry.Seed(IntegrationHive.User, ContextMenuModule.Scopes[scope] + @"Rider\command", "", '"' + Files["rider64.exe"] + "\" \"%1\"");
            Registry.Seed(IntegrationHive.User, ContextMenuModule.Scopes["files"] + @"Unrelated\command", "", UnrelatedCommand);
            Registry.Seed(IntegrationHive.User, LinkRegistrations.Classes + "magnet", "URL Protocol", "");
            Registry.Seed(IntegrationHive.User, LinkRegistrations.Classes + @"magnet\shell\open\command", "", '"' + Files["qbittorrent.exe"] + "\" \"%1\"");
            Registry.Seed(IntegrationHive.User, @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\vscode\UserChoice", "ProgId", "Code.Protocol");
            Registry.Seed(IntegrationHive.Machine, LinkRegistrations.Classes + @"Code.Protocol\shell\open\command", "", '"' + Files["Code.exe"] + "\" \"%1\"");
        }
        internal ModulePage ContextPage() => new ContextMenuModule(Registry, Discovery).CreatePage(Context);
        internal ModulePage LinkPage() => new LinkHandlersModule(Registry, Discovery).CreatePage(Context);
    }
    private sealed class FakeDiscovery(IReadOnlyDictionary<string, string> files) : IInstalledAppDiscovery
    {
        internal int Calls;
        public IReadOnlyDictionary<string, string> Find(IEnumerable<string> executables, string root, CancellationToken token)
        {
            Calls++;
            token.ThrowIfCancellationRequested();
            return files;
        }
    }
    private sealed class MemoryRegistry : IIntegrationRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);
        internal int Writes;
        private static string Key(IntegrationHive hive, string path) => hive + ":" + path.TrimEnd('\\');
        internal void Seed(IntegrationHive hive, string path, string name, string value)
        {
            var key = Key(hive, path);
            if (!keys.TryGetValue(key, out var values))
                keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            values[name] = value;
        }
        public string? Read(IntegrationHive hive, string path, string name = "") => keys.TryGetValue(Key(hive, path), out var values) ? values.GetValueOrDefault(name) : null;
        public bool Exists(IntegrationHive hive, string path) => keys.Keys.Any(k => k.Equals(Key(hive, path), StringComparison.OrdinalIgnoreCase) || k.StartsWith(Key(hive, path) + "\\", StringComparison.OrdinalIgnoreCase));
        public string[] SubKeys(IntegrationHive hive, string path)
        {
            var prefix = Key(hive, path) + "\\";
            return keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public void Write(string path, string name, string value)
        {
            Writes++;
            Seed(IntegrationHive.User, path, name, value);
        }
        public void DeleteTree(string path)
        {
            Writes++;
            var root = Key(IntegrationHive.User, path);
            foreach (var k in keys.Keys.Where(k => k.Equals(root, StringComparison.OrdinalIgnoreCase) || k.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
                keys.Remove(k);
        }
        public void DeleteValue(string path, string name)
        {
            Writes++;
            if (keys.TryGetValue(Key(IntegrationHive.User, path), out var values))
                values.Remove(name);
        }
    }
    private sealed class TestContext(string directory) : IModuleContext
    {
        private readonly ConcurrentDictionary<string, string> settings = new();
        private CancellationTokenSource? source;
        private volatile bool busy;
        internal string Report = "";
        internal string Opened = "";
        public string DataDirectory => directory;
        public string Get(string key, string fallback = "") => settings.GetValueOrDefault(key, fallback);
        public void Save(string key, string value) => settings[key] = value;
        public void Log(string module, string message)
        {
        }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task ShowReportAsync(string title, string content)
        {
            Report = content;
            return Task.CompletedTask;
        }
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) => throw new InvalidOperationException("Tests must not launch system actions.");
        public void Open(string path) => Opened = path;
        public bool IsOperationRunning => busy;
        public CancellationToken OperationToken => source?.Token ?? CancellationToken.None;
        public bool TryBeginOperation()
        {
            if (busy)
                return false;
            source = new();
            busy = true;
            return true;
        }
        public void EndOperation()
        {
            source?.Dispose();
            source = null;
            busy = false;
        }
    }
}
