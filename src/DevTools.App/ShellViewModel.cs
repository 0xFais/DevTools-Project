using DevTools.Contracts;
using DevTools.Infrastructure;
using DevTools.Modules;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DevTools.App;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly AppModuleContext context;
    private readonly bool autoRefresh;
    public bool AutoRefresh => autoRefresh;
    private readonly ExtensionLoader loader = new();
    private readonly DispatcherTimer themeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ModulePage selected;
    private string activity = "";
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingActivity = new();
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool busy;
    public ObservableCollection<ModulePage> Pages { get; } = [];
    public ObservableCollection<NavigationNode> Sidebar { get; } = [];
    public ModulePage SettingsPage => Pages.Single(p => p.Id == "settings");
    public bool IsSettingsSelected => Selected.Id == "settings";
    public ICollectionView Navigation
    {
        get;
    }
    public ModulePage Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            foreach (var node in Sidebar) node.Reveal(value.Id);
            Raise(nameof(IsSettingsSelected));
            if (autoRefresh && value.WindowsWorkspace is { IsInitialized: false, RefreshAction: { } refresh } && refresh.CanExecute(null)) refresh.Execute(null);
        }
    }
    public string Activity
    {
        get => activity; private set => Set(ref activity, value);
    }
    public bool IsBusy
    {
        get => busy; private set => Set(ref busy, value);
    }
    public ICommand CancelCommand
    {
        get;
    }
    public ICommand NavigateCommand
    {
        get;
    }
    public ShellViewModel(AppModuleContext context, bool autoRefresh = true)
    {
        this.context = context;
        this.autoRefresh = autoRefresh;
        context.Logged += OnLog;
        activityTimer.Tick += (_, _) => FlushActivity();
        activityTimer.Start();
        context.OperationChanged += () => { IsBusy = context.IsOperationRunning; foreach (var page in Pages) foreach (var action in page.Actions) action.Notify(); };
        CancelCommand = new ShellCommand(_ => context.Cancel());
        NavigateCommand = new ShellCommand(page => { if (page is ModulePage p) Selected = p; });
        Pages.Add(new("overview", "Workspace", "Everything you need to make this machine your own.", "Workspace", "\uE80F"));
        foreach (var module in BuiltInModules.Create())
            AddModule(module);
        foreach (var module in loader.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "Extensions"), id => context.Get("extensions.enabled." + id) == "true", message => context.Log("Extensions", message)))
            AddModule(module);
        selected = Pages.First(p => p.Id == "overview");
        BuildSidebar();
        foreach (var node in Sidebar) node.Reveal(selected.Id);
        Navigation = CollectionViewSource.GetDefaultView(Pages);
        Navigation.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModulePage.Group)));
        context.Log("DevTools", "Workspace ready. Administrator session.");
        themeTimer.Tick += (_, _) => { if (context.Get("settings.theme", "Dark") == "System") ApplyTheme(); };
        themeTimer.Start();
    }
    private void BuildSidebar()
    {
        NavigationNode Leaf(string id) { var page = Pages.Single(p => p.Id == id); return new(page.Id, page.Title, page); }
        NavigationNode Group(string id, string title) => new(id, title, expanded: context.Get("navigation.expanded." + id, "true") == "true", saveExpansion: (key, value) => context.Save("navigation.expanded." + key, value ? "true" : "false"));
        Sidebar.Add(Leaf("overview"));
        var apps = Group("apps", "Apps Manager"); apps.Children.Add(Leaf("scoop")); Sidebar.Add(apps);
        var developer = Group("developer-tools", "Developer Tools"); developer.Children.Add(Leaf("binaries")); Sidebar.Add(developer);
        var preferences = Group("windows-preferences", "Windows Preferences"); preferences.Children.Add(Leaf("start-search"));
        foreach (var page in DevTools.Modules.Windows.WindowsPreferencesModule.Pages) preferences.Children.Add(Leaf(page.Id));
        Sidebar.Add(preferences);
        var windowsTools = Group("windows-tools", "Windows Tools");
        foreach (var id in new[] { "user-folders", "windows-updates", "activation", "context-menu", "link-handlers" })
            if (Pages.FirstOrDefault(p => p.Id == id) is { } page) windowsTools.Children.Add(new(page.Id, page.Title, page));
        // Keep newly added Windows modules visible in the tools group automatically.
        var known = Sidebar.SelectMany(Flatten).Concat(Flatten(windowsTools)).Where(n => n.Page is not null).Select(n => n.Page!.Id).Append("settings").ToHashSet();
        foreach (var page in Pages.Where(p => p.Group == "Windows" && !known.Contains(p.Id))) { windowsTools.Children.Add(new(page.Id, page.Title, page)); known.Add(page.Id); }
        Sidebar.Add(windowsTools);
        foreach (var grouping in Pages.Where(p => !known.Contains(p.Id)).GroupBy(p => p.Group))
        {
            var group = Group("extension-" + grouping.Key, grouping.Key);
            foreach (var page in grouping) group.Children.Add(new(page.Id, page.Title, page));
            Sidebar.Add(group);
        }
    }
    private static IEnumerable<NavigationNode> Flatten(NavigationNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    private void AddModule(IDevToolsModule module)
    {
        try
        {
            if (Pages.Any(p => p.Id == module.Id))
                throw new InvalidOperationException("Duplicate module ID: " + module.Id);
            var page = module.CreatePage(context);
            if (page.Id != module.Id)
                throw new InvalidOperationException("Page ID must match module ID.");
            Pages.Add(page);
        }
        catch (Exception e) { context.Log("Modules", module.Id + ": " + e.Message); }
    }
    private void OnLog(string module, string line) => pendingActivity.Enqueue(line);
    private void FlushActivity()
    {
        var batch = new System.Text.StringBuilder();
        for (var i = 0; i < 250 && pendingActivity.TryDequeue(out var line); i++) batch.AppendLine(line);
        if (batch.Length == 0) return;
        var next = Activity + batch;
        Activity = next.Length > 30000 ? next[^24000..] : next;
    }
    public void ApplyTheme()
    {
        var mode = context.Get("settings.theme", "Dark");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var light = mode == "Light" || (mode == "System" && key?.GetValue("AppsUseLightTheme") is int n && n == 1);
        var colors = light ? new[] { "#F3F4F8", "#E9ECF3", "#FFFFFF", "#222637", "#5C6375", "#D8DCE7", "#6254CF", "#FFFFFF", "#E6E1FA", "#23744F" } : new[] { "#15171C", "#111318", "#1D2028", "#EDEEF5", "#A1A7B8", "#30343E", "#AAA1FF", "#191529", "#2D2845", "#97D9BD" };
        var names = new[] { "AppBackground", "SidebarBackground", "PanelBackground", "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "OnAccentBrush", "SelectionBrush", "SuccessBrush" };
        for (var i = 0; i < names.Length; i++)
            Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    public void Dispose() { themeTimer.Stop(); activityTimer.Stop(); context.Logged -= OnLog; }
}
public sealed class ShellCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true; public void Execute(object? parameter) => execute(parameter); public event EventHandler? CanExecuteChanged { add { } remove { } }
}
