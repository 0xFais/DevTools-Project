using DevTools.Contracts;
using DevTools.Infrastructure;
using DevTools.Modules;
using DevTools.Modules.Integrations;
using DevTools.Modules.Windows;
using DevTools.App;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DevTools.Tests;

internal static class Program
{
    private static int count;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--echo")
        {
            Console.Write(args[1]);
            return 0;
        }
        if (args.FirstOrDefault() == "--sleep")
        {
            Thread.Sleep(30000);
            return 0;
        }
        if (args.FirstOrDefault() == "--json-stderr")
        {
            Console.Write("{\"Code\":2,\"Count\":0}");
            Console.Error.Write("#< CLIXML\n<Objs>PowerShell progress</Objs>");
            return 0;
        }
        try
        {
            if (args.FirstOrDefault() == "--portable-smoke") { PortableStorageTests.Smoke(args[1]); return 0; }
            if (args.FirstOrDefault() == "--preferences-readonly")
            {
                var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
                var result = WindowsPreferencesModule.Pages.Select(p =>
                {
                    var backup = Path.Combine(directory, p.Id + "-unused.json");
                    var state = new WindowsPreferencesService(new WindowsPreferencesBackend(), p.Id, backup).Read();
                    Check(!File.Exists(backup)); return new { p.Id, Snapshot = state };
                }).ToArray();
                File.WriteAllText(Path.Combine(directory, "windows-preferences-readonly.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS: all 17 Windows preferences inspected without writes or Explorer restart."); return 0;
            }
            if (args.FirstOrDefault() == "--start-search-readonly")
            {
                var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
                var backup = Path.Combine(directory, "unused-start-search-backup.json");
                var snapshot = new StartSearchService(new StartSearchRegistryBackend(), backup).Read();
                Check(!File.Exists(backup));
                File.WriteAllText(Path.Combine(directory, "start-search-readonly.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS: native Start/search status and compatibility were read without applying settings or creating a backup. " + snapshot.Platform);
                return 0;
            }
            if (args.FirstOrDefault() == "--windows-readonly") { WindowsControlTests.ReadOnlySmoke(args[1]); return 0; }
            var repo = Path.GetFullPath(args.FirstOrDefault() ?? ".");
            var artifacts = Path.Combine(repo, "artifacts", "tests");
            Directory.CreateDirectory(artifacts);
            Run("Context commands and legacy Rider registry key", () => { var app = new AppProfile("rider", "Rider", "rider64.exe", "editor", "Rider"); Check(ContextMenuModule.RegistryKeyName(app) == "Rider"); Check(WindowsEnvironment.ContextCommand("rider", @"C:\Some Folder\rider64.exe", "background") == "\"C:\\Some Folder\\rider64.exe\" \"%V\""); Throws<ArgumentException>(() => WindowsEnvironment.ContextCommand("rider", "bad\"path", "files")); });
            Run("Settings round-trip and INI import parser", () => { var dir = Path.Combine(artifacts, "settings"); var store = new SettingsStore(dir); store.Save("a", "value with spaces"); Check(new SettingsStore(dir).Get("a") == "value with spaces"); var ini = Path.Combine(dir, "old.ini"); File.WriteAllText(ini, "[settings]\ntheme=Dark\n; comment\n[paths]\npgdata=C:\\PostgreSQL\\data\n"); Check(SettingsStore.ReadIni(ini)["settings"]["theme"] == "Dark"); });
            Run("Process arguments are passed literally", () => { var input = "a & b \"quoted\" %PATH%"; var r = ProcessRunner.RunAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "--echo", input], CancellationToken.None).GetAwaiter().GetResult(); Check(r.ExitCode == 0 && r.Output == input); });
            Run("JSON stdout is isolated from PowerShell diagnostic stderr", () => {
                var r = ProcessRunner.RunAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "--json-stderr"], CancellationToken.None).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(r.EnsureStandardOutputSuccess());
                Check(doc.RootElement.GetProperty("Code").GetInt32() == 2 && r.StandardError!.StartsWith("#< CLIXML") && r.Output.Contains("#< CLIXML"));
                Throws<InvalidOperationException>(() => (r with { ExitCode = 1 }).EnsureStandardOutputSuccess());
            });
            Run("Cancellation stops a running process", () => { using var cts = new CancellationTokenSource(400); var watch = Stopwatch.StartNew(); Throws<OperationCanceledException>(() => ProcessRunner.RunAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "--sleep"], cts.Token).GetAwaiter().GetResult()); Check(watch.Elapsed < TimeSpan.FromSeconds(8)); });
            Run("Extension isolation, opt-in and API validation", () =>
            {
                var folder = Path.Combine(artifacts, "Extensions", "Sample");
                Directory.CreateDirectory(folder);
                var built = Path.Combine(repo, "extensions", "DevTools.SampleExtension", "bin", "Release", "net10.0-windows");
                foreach (var file in Directory.GetFiles(built).Where(f => f.EndsWith(".dll") || f.EndsWith(".json")))
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), true);
                var loader = new ExtensionLoader();
                var logs = new List<string>();
                Check(!loader.Load(Path.GetDirectoryName(folder)!, _ => false, logs.Add).Any());
                var modules = loader.Load(Path.GetDirectoryName(folder)!, _ => true, logs.Add).ToArray();
                Check(modules.Length == 1 && modules[0].Id == "sample.system-info");
                File.WriteAllText(Path.Combine(folder, "extension.json"), "{\"id\":\"sample.system-info\",\"name\":\"Bad API\",\"entryAssembly\":\"DevTools.SampleExtension.dll\",\"apiVersion\":999}");
                Check(!loader.Load(Path.GetDirectoryName(folder)!, _ => true, logs.Add).Any());
                Check(logs.Any(l => l.Contains("Unsupported extension API")));
            });
            FolderInspectorTests.RunAll(artifacts, Run);
            ScoopSetupTests.RunAll(artifacts, Run);
            ScoopTests.RunAll(artifacts, Run);
            IntegrationTests.RunAll(artifacts, Run);
            StartSearchTests.RunAll(artifacts, Run);
            WindowsPreferencesTests.RunAll(artifacts, Run);
            PortableStorageTests.RunAll(artifacts, Run);
            WindowsControlTests.RunAll(artifacts, Run);
            UpdatePolicyTests.RunAll(artifacts, Run);
            UpdatePauseTests.RunAll(artifacts, Run);
            var app = new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevTools;component/Themes/Studio.xaml", UriKind.Relative) });
            var uiContext = new AppModuleContext(Path.Combine(artifacts, "ui-data"));
            uiContext.Save("settings.theme", "Dark");
            using var shell = new ShellViewModel(uiContext, autoRefresh: false);
            shell.ApplyTheme();
            var window = new MainWindow { DataContext = shell };
            Run("Sidebar starts on Workspace and groups Windows tools cleanly", () =>
            {
                Check(shell.Selected.Id == "overview");
                Check(shell.Sidebar.Select(n => n.Id).SequenceEqual(new[] { "overview", "apps", "developer-tools", "windows-preferences", "windows-tools" }));
                Check(shell.Sidebar.Single(n => n.Id == "developer-tools").Children.Select(n => n.Id).SequenceEqual(new[] { "binaries", "environment-path", "sdks-runtimes" }));
                var preferences = shell.Sidebar.Single(n => n.Id == "windows-preferences");
                Check(preferences.Children.Select(n => n.Id).SequenceEqual(new[] { "start-search", "explorer-taskbar", "appearance-signin", "privacy-suggestions", "power-gaming", "developer-settings" }));
                var windowsTools = shell.Sidebar.Single(n => n.Id == "windows-tools");
                Check(windowsTools.Children.Select(n => n.Id).SequenceEqual(new[] { "user-folders", "windows-updates", "activation", "context-menu", "link-handlers" }));
                Check(shell.SettingsPage.Id == "settings" && !shell.Sidebar.Any(n => n.Id == "settings"));
                Render(window, Path.Combine(artifacts, "workspace-startup.png"), 1180, 820);
            });
            Run("Sidebar remembers collapsed groups and reveals selected pages", () =>
            {
                var preferences = shell.Sidebar.Single(n => n.Id == "windows-preferences"); var windowsTools = shell.Sidebar.Single(n => n.Id == "windows-tools");
                preferences.IsExpanded = false; windowsTools.IsExpanded = false;
                Check(uiContext.Get("navigation.expanded.windows-preferences") == "false" && uiContext.Get("navigation.expanded.windows-tools") == "false");
                using (var restored = new ShellViewModel(uiContext, autoRefresh: false))
                    Check(!restored.Sidebar.Single(n => n.Id == "windows-preferences").IsExpanded && !restored.Sidebar.Single(n => n.Id == "windows-tools").IsExpanded && restored.Selected.Id == "overview");
                shell.Selected = shell.Pages.Single(p => p.Id == "context-menu");
                Check(windowsTools.IsExpanded && windowsTools.Children.Single(n => n.Id == "context-menu").IsSelected && !preferences.IsExpanded);
                windowsTools.IsExpanded = false; shell.Selected = shell.Pages.Single(p => p.Id == "windows-updates");
                Check(windowsTools.IsExpanded && windowsTools.Children.Single(n => n.Id == "windows-updates").IsSelected);
                Render(window, Path.Combine(artifacts, "sidebar-windows-tools-expanded.png"), 1180, 820);
                shell.Selected = shell.SettingsPage; Check(shell.IsSettingsSelected);
                shell.Selected = shell.Pages.Single(p => p.Id == "overview");
            });
            Run("Sidebar group headers toggle with a single press across the full row", () =>
            {
                Layout(window, 1180, 820);
                var tree = Descendants((FrameworkElement)window.Content).OfType<TreeView>().Single();
                var model = shell.Sidebar.Single(n => n.Id == "windows-tools");
                var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(model);
                item.ApplyTemplate();
                var row = (Border)item.Template.FindName("row", item);
                var before = model.IsExpanded;
                var first = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                row.RaiseEvent(first);
                Check(first.Handled && model.IsExpanded != before);
                var second = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                row.RaiseEvent(second);
                Check(second.Handled && model.IsExpanded == before);
            });
            Run("Every built-in module constructs and every page lays out", () =>
            {
                Check(shell.Pages.Count == 17);
                Check(shell.Pages.Any(p => p.Id == "context-menu"));
                Check(shell.Pages.Single(p => p.Id == "scoop").Group == "Apps Manager");
                Check(shell.Pages.Single(p => p.Id == "environment-path").HasEnvironmentPathWorkspace);
                Check(shell.Pages.Single(p => p.Id == "sdks-runtimes").HasSdkCatalogWorkspace);
                var contextPage = shell.Pages.Single(p => p.Id == "context-menu");
                Check(contextPage.Browser!.CatalogNames.Contains("JetBrains Rider"));
                foreach (var page in shell.Pages)
                {
                    shell.Selected = page;
                    Layout(window, 1180, 820);
                    Check(((FrameworkElement)window.Content).ActualWidth == 1180);
                }
            });
            Run("Developer binary inventory renders and filters in dark, light and minimum sizes", () =>
            {
                var page = shell.Pages.Single(p => p.Id == "binaries");
                var workspace = page.BinaryInventoryWorkspace!;
                Check(workspace.Items.Count >= 70 && workspace.Items.Any(i => i.Name == "GCC") && workspace.Items.Any(i => i.Name == "MSVC C/C++"));
                Check(!workspace.Commands.ContainsKey("Install"));
                workspace.Items[0].Status = "Installed · 2.51.0"; workspace.Items[0].Path = @"C:\Scoop\apps\git\current\cmd\git.exe"; workspace.Items[0].Source = "Scoop";
                workspace.Items[1].Status = "Not installed through Scoop"; workspace.Items[2].Status = "Not installed through Scoop"; workspace.Items[3].Status = "Installed · 10.0.100"; workspace.Items[3].Path = @"C:\Scoop\apps\dotnet-sdk\current\dotnet.exe"; workspace.Items[3].Version = "10.0.100"; workspace.Items[3].Details = "SDKs (2):\n10.0.100 [C:\\Scoop\\apps\\dotnet-sdk\\current\\sdk]\n9.0.300 [C:\\Scoop\\apps\\dotnet-sdk\\current\\sdk]\n\nRuntimes (2):\nMicrosoft.NETCore.App 10.0.0 [C:\\Scoop\\apps\\dotnet-sdk\\current\\shared\\Microsoft.NETCore.App]\nMicrosoft.AspNetCore.App 10.0.0 [C:\\Scoop\\apps\\dotnet-sdk\\current\\shared\\Microsoft.AspNetCore.App]";
                workspace.Search = "git";
                Check(workspace.VisibleItems.Count == 1 && workspace.VisibleItems.All(i => i.Status.StartsWith("Installed", StringComparison.Ordinal)));
                workspace.Search = "";
                workspace.Selected = workspace.Items[3];
                shell.Selected = page;
                Render(window, Path.Combine(artifacts, "binaries-dark.png"), 1180, 820);
                Render(window, Path.Combine(artifacts, "binaries-minimum.png"), 1024, 700);
                uiContext.Save("settings.theme", "Light"); shell.ApplyTheme(); Render(window, Path.Combine(artifacts, "binaries-light.png"), 1180, 820);
                uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme();
            });
            Run("Environment and SDK inspectors render in dark, light and minimum sizes", () =>
            {
                var environmentPage = shell.Pages.Single(p => p.Id == "environment-path");
                var environment = environmentPage.EnvironmentPathWorkspace!;
                Check(environment.SelectedVariable?.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) == true);
                Check(environment.PathEntries.Count > 0);
                shell.Selected = environmentPage;
                Render(window, Path.Combine(artifacts, "environment-path-dark.png"), 1180, 820);
                Render(window, Path.Combine(artifacts, "environment-path-minimum.png"), 1024, 700);

                var sdkPage = shell.Pages.Single(p => p.Id == "sdks-runtimes");
                var sdk = sdkPage.SdkCatalogWorkspace!;
                WaitForUi(() => sdk.Ecosystems.Count == 5 && sdk.DotnetSdks.Count > 0 && sdk.DotnetRuntimes.Count > 0, TimeSpan.FromSeconds(15));
                Check(sdk.Ecosystems.Count == 5 && sdk.SelectedEcosystem?.Name == ".NET");
                Check(sdk.DotnetSdks.Count > 0 && sdk.SdkDetails.Contains(sdk.DotnetSdks[0].Version));
                Check(sdk.DotnetRuntimes.Count > 0 && sdk.RuntimeDetails.Contains(sdk.DotnetRuntimes[0].Version));
                Check(sdk.HostVersion != "Not detected" && sdk.Architecture != "—" && sdk.Rid != "—");
                shell.Selected = sdkPage;
                Render(window, Path.Combine(artifacts, "sdk-catalog-dark.png"), 1180, 820);
                Render(window, Path.Combine(artifacts, "sdk-catalog-minimum.png"), 1024, 700);
                uiContext.Save("settings.theme", "Light"); shell.ApplyTheme();
                Render(window, Path.Combine(artifacts, "sdk-catalog-light.png"), 1180, 820);
                uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme();
            });
            shell.Selected = shell.SettingsPage;
            Render(window, Path.Combine(artifacts, "settings-scoop-managed.png"), 1180, 820);
            shell.Selected = shell.Pages.Single(p => p.Id == "overview");
            Run("Dark, light and minimum-size WPF render", () =>
            {
                Render(window, Path.Combine(artifacts, "developer-studio-dark.png"), 1180, 820);
                Render(window, Path.Combine(artifacts, "developer-studio-compact.png"), 1024, 700);
                uiContext.Save("settings.theme", "Light");
                shell.ApplyTheme();
                Render(window, Path.Combine(artifacts, "developer-studio-light.png"), 1180, 820);
                uiContext.Save("settings.theme", "Dark");
                shell.ApplyTheme();
                shell.Selected = shell.Pages.Single(p => p.Id == "context-menu");
                Render(window, Path.Combine(artifacts, "context-menu.png"), 1180, 820);
            });
            Run("Scoop compact table dark, light and minimum-size render", () => { var page = ScoopTests.Preview(artifacts); var scoopIndex = shell.Pages.ToList().FindIndex(p => p.Id == "scoop"); shell.Pages[scoopIndex] = page; shell.Selected = page; Render(window, Path.Combine(artifacts, "scoop-dark.png"), 1180, 820); Render(window, Path.Combine(artifacts, "scoop-minimum.png"), 1024, 700); uiContext.Save("settings.theme", "Light"); shell.ApplyTheme(); Render(window, Path.Combine(artifacts, "scoop-light.png"), 1180, 820); for (var tab = 1; tab < 4; tab++) { page.ScoopWorkspace!.Tab = tab; Render(window, Path.Combine(artifacts, "scoop-tab-" + tab + ".png"), 1180, 820); } page.ScoopWorkspace!.Tab = 0; uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme(); });
            Run("Scoop setup screens render ready, missing PATH, missing installation and repair states", () =>
            {
                var page = ScoopTests.Preview(artifacts); var index = shell.Pages.ToList().FindIndex(p => p.Id == "scoop"); shell.Pages[index] = page; shell.Selected = page;
                foreach (var state in new[] { ScoopSetupState.Ready, ScoopSetupState.PathMissing, ScoopSetupState.Missing, ScoopSetupState.Broken })
                {
                    page.ScoopWorkspace!.SetupState = state; page.ScoopWorkspace.SetupMessage = state == ScoopSetupState.PathMissing ? "Add the Scoop shims folder to your user PATH." : state == ScoopSetupState.Missing ? "Install Scoop or locate an existing installation." : state == ScoopSetupState.Broken ? "The selected installation is incomplete. Choose another folder." : "Scoop is installed and available in your user PATH.";
                    Render(window, Path.Combine(artifacts, "scoop-setup-" + state + ".png"), 1180, 820);
                    Render(window, Path.Combine(artifacts, "scoop-setup-" + state + "-minimum.png"), 1024, 700);
                }
            });
            Run("Folder inspector renders in dark, light and minimum window sizes", () =>
            {
                var page = FolderInspectorTests.Preview(artifacts); shell.Pages[shell.Pages.ToList().FindIndex(p => p.Id == "user-folders")] = page; shell.Selected = page;
                foreach (var theme in new[] { "Dark", "Light" }) { uiContext.Save("settings.theme", theme); shell.ApplyTheme(); Render(window, Path.Combine(artifacts, "folders-" + theme + ".png"), 1180, 820); Render(window, Path.Combine(artifacts, "folders-" + theme + "-minimum.png"), 1024, 700); }
                uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme();
            });
            Run("Start/search compact rows render in dark, light, minimum-size and managed states", () =>
            {
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    uiContext.Save("settings.theme", theme); shell.ApplyTheme();
                    var page = StartSearchTests.Preview(artifacts);
                    shell.Pages[shell.Pages.ToList().FindIndex(p => p.Id == page.Id)] = page; shell.Selected = page;
                    Render(window, Path.Combine(artifacts, "start-search-" + theme + ".png"), 1180, 820);
                    Render(window, Path.Combine(artifacts, "start-search-" + theme + "-minimum.png"), 1024, 700);
                    var switches = Descendants((FrameworkElement)window.Content).OfType<CheckBox>().Where(b => b.Name is "BingSwitch" or "RecommendationsSwitch" && ParticipatesInLayout(b)).ToArray();
                    Check(switches.Length == 2 && switches.All(b => b.IsEnabled));
                }
                var managed = StartSearchTests.Preview(artifacts, managed: true); shell.Pages[shell.Pages.ToList().FindIndex(p => p.Id == managed.Id)] = managed; shell.Selected = managed;
                Render(window, Path.Combine(artifacts, "start-search-managed.png"), 1180, 820);
                Check(!managed.StartSearchWorkspace!.CanChangeBing && !managed.StartSearchWorkspace.CanChangeRecommendations);
                uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme();
            });
            Run("Integrated Windows preferences render with switches and choices in both themes and minimum size", () =>
            {
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    uiContext.Save("settings.theme", theme); shell.ApplyTheme();
                    foreach (var definition in WindowsPreferencesModule.Pages)
                    {
                        var page = WindowsPreferencesTests.Preview(artifacts, definition.Id);
                        shell.Pages[shell.Pages.ToList().FindIndex(p => p.Id == page.Id)] = page; shell.Selected = page;
                        Render(window, Path.Combine(artifacts, definition.Id + "-" + theme + ".png"), 1180, 820);
                        Render(window, Path.Combine(artifacts, definition.Id + "-" + theme + "-minimum.png"), 1024, 700);
                        var apply = Descendants((FrameworkElement)window.Content).OfType<Button>().Single(b => b.Name == "PreferencesApplyButton" && ParticipatesInLayout(b));
                        var position = apply.TransformToAncestor((FrameworkElement)window.Content).Transform(new Point());
                        Check(position.Y > 0 && position.Y + apply.ActualHeight < 532 && position.X + apply.ActualWidth <= 1024);
                        Check(page.PreferencesWorkspace!.Rows.Count == WindowsPreferenceDefinitions.All.Count(d => d.Page == definition.Id));
                    }
                }
                uiContext.Save("settings.theme", "Dark"); shell.ApplyTheme();
            });
            PopupTests.RunAll(artifacts, shell, Run);
            WindowBoundsTests.RunAll(shell, Run);
            Run("Administrator manifest requested", () => { var manifest = File.ReadAllText(Path.Combine(repo, "src", "DevTools.App", "app.manifest")); Check(manifest.Contains("requireAdministrator")); });
            Run("Split inspector WPF renders with registered and available test apps", () =>
            {
                foreach (var links in new[] { false, true })
                {
                    var page = IntegrationTests.Preview(artifacts, links);
                    var index = shell.Pages.ToList().FindIndex(p => p.Id == page.Id);
                    shell.Pages[index] = page;
                    shell.Selected = page;
                    Render(window, Path.Combine(artifacts, links ? "link-handlers-split.png" : "context-menu-split.png"), 1180, 820);
                    VerifyToolbar((FrameworkElement)window.Content);
                    Run(links ? "Link search WPF binding and clear action" : "Context search WPF binding and clear action", () =>
                    {
                        var search = Descendants((FrameworkElement)window.Content).OfType<TextBox>().Single(b => b.Name == "SearchBox" && b.ActualWidth > 0 && ParticipatesInLayout(b));
                        Check(System.Windows.Data.BindingOperations.GetBinding(search, TextBox.TextProperty)!.UpdateSourceTrigger == System.Windows.Data.UpdateSourceTrigger.PropertyChanged);
                        var query = links ? "magnet" : "rider";
                        search.SetCurrentValue(TextBox.TextProperty, query);
                        search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                        Layout(window, 1180, 820);
                        Check(page.Browser!.SearchText == query && page.Browser.Items.Count == (links ? 2 : 1));
                        Render(window, Path.Combine(artifacts, links ? "link-handlers-search-filtered.png" : "context-menu-search-filtered.png"), 1180, 820);
                        page.Browser.ClearSearchCommand.Execute(null);
                        Layout(window, 1180, 820);
                        Check(search.Text == "" && page.Browser.Items.Count > 1);
                    });
                    Render(window, Path.Combine(artifacts, links ? "link-handlers-split-compact.png" : "context-menu-split-compact.png"), 1024, 700);
                    VerifyToolbar((FrameworkElement)window.Content);
                }
            });
            Run("Secret fields excluded from settings persistence", () => { var page = shell.Pages.Single(p => p.Id == "activation"); Check(page["key"].Kind == FieldKind.Secret); Check(!File.ReadAllText(Path.Combine(uiContext.DataDirectory, "settings.json")).Contains("activation.key")); });
            Run("Compact Windows pages render update and activation states", () => {
                foreach (var activation in new[] { false, true }) foreach (var state in activation ? new[] { "unlicensed", "activated", "home" } : new[] { "enabled", "paused", "disabled", "partial", "custom" }) {
                    var page = WindowsControlTests.Preview(artifacts, activation, state); var index = shell.Pages.ToList().FindIndex(p => p.Id == page.Id); shell.Pages[index] = page; shell.Selected = page;
                    Render(window, Path.Combine(artifacts, (activation ? "activation-" : "updates-") + state + ".png"), 1180, 820);
                    Render(window, Path.Combine(artifacts, (activation ? "activation-" : "updates-") + state + "-minimum.png"), 1024, 700);
                    Render(window, Path.Combine(artifacts, (activation ? "activation-" : "updates-") + state + ".png"), 1180, 820);
                    Check(Descendants((FrameworkElement)window.Content).OfType<TextBlock>().Any(t => t.Name == "StatusHeading" && t.ActualWidth > 0));
                    var badge = Descendants((FrameworkElement)window.Content).OfType<TextBlock>().Single(t => t.Name == "StatusBadge"); Check(badge.Text == page.WindowsWorkspace!.Badge && badge.ActualWidth > 0);
                    if (!activation)
                    {
                        var apply = Descendants((FrameworkElement)window.Content).OfType<Button>().Single(b => b.Name == "PauseUpdatesButton");
                        Check(apply.IsEnabled == (state != "partial"));
                        var duration = Descendants((FrameworkElement)window.Content).OfType<ComboBox>().Single(b => b.Name == "PolicyDuration");
                        duration.SelectedItem = "Custom date";
                        Check(page["duration"].Value == "Custom date" && page["restoreDate"].IsVisible);
                        var date = Descendants((FrameworkElement)window.Content).OfType<TextBox>().Single(b => b.Name == "PolicyRestoreDate");
                        date.Text = "2028-12-01"; date.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                        Check(page["restoreDate"].Value == "2028-12-01");
                    }
                    if (activation) { var box = Descendants((FrameworkElement)window.Content).OfType<PasswordBox>().Single(t => t.Name == "CustomKeyBox"); box.Password = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"; Check(page["key"].Value == box.Password); page["key"].Value = ""; Check(box.Password.Length == 0); }
                }
            });
            window.Close();
            Console.WriteLine($"PASS: {count} checks. Renders: {artifacts}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Run(string name, Action action)
    {
        action();
        count++;
        Console.WriteLine("PASS " + name);
    }
    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Assertion failed.");
    }
    private static void WaitForUi(Func<bool> condition, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Check(condition());
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
    private static void Layout(MainWindow window, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }
    private static void Render(MainWindow window, string path, int width, int height)
    {
        Layout(window, width, height);
        // A second pass measures text after deferred data bindings have settled.
        Layout(window, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((FrameworkElement)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
    private static bool ParticipatesInLayout(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static void VerifyToolbar(FrameworkElement root)
    {
        var buttons = Descendants(root).OfType<Button>().Where(b => b.Content?.ToString() == "Scan installed apps" && b.ActualWidth > 0 && ParticipatesInLayout(b)).ToArray();
        Check(buttons.Length == 1);
        DependencyObject? ancestor = buttons[0];
        while (ancestor is not ItemsControl)
            ancestor = VisualTreeHelper.GetParent(ancestor!) ?? throw new InvalidOperationException("Toolbar container missing.");
        Check(((ItemsControl)ancestor).ActualWidth >= buttons[0].ActualWidth);
        var origin = buttons[0].TransformToAncestor(root).Transform(new Point());
        Check(origin.X >= 0 && origin.X + buttons[0].ActualWidth <= root.ActualWidth);
        var search = Descendants(root).OfType<TextBox>().Single(b => b.Name == "SearchBox" && b.ActualWidth > 0 && ParticipatesInLayout(b));
        var searchOrigin = search.TransformToAncestor(root).Transform(new Point());
        foreach (var button in Descendants(root).OfType<Button>().Where(b => b.ActualWidth > 0 && ParticipatesInLayout(b) && b.Content?.ToString() is "Scan installed apps" or "Refresh added" or "Add custom"))
        {
            var position = button.TransformToAncestor(root).Transform(new Point());
            Check(Math.Abs(position.Y + button.ActualHeight / 2 - searchOrigin.Y - search.ActualHeight / 2) <= 1);
            Check(position.X >= searchOrigin.X + search.ActualWidth && position.X + button.ActualWidth <= root.ActualWidth);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}


