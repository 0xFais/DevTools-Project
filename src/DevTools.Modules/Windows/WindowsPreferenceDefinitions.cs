using Microsoft.Win32;
using System.Text.Json;

namespace DevTools.Modules.Windows;

public sealed record PreferenceResource(string Id, string Kind, bool Machine = false, string Path = "", string Name = "");
public sealed record PreferenceChange(PreferenceResource Resource, StartSearchValue? On, StartSearchValue? Off);
public sealed record PreferenceDefinition(string Id, string Page, string Title, string Description, string SourceId, IReadOnlyList<PreferenceChange> Changes,
    bool DefaultOn = false, bool RestartExplorer = false, bool RestartWindows = false, int MinBuild = 19041, bool EnterpriseOnly = false, IReadOnlyList<string>? Choices = null)
{
    public bool RequiresAdmin => Changes.Any(c => c.Resource.Machine);
    public string Scope => (RequiresAdmin ? "All users · administrator" : "Current user") + (RestartExplorer ? " · Explorer restart" : "") + (RestartWindows ? " · Windows/app restart may be required" : "");
}

public static class WindowsPreferenceDefinitions
{
    public const string SourceCommit = "2f0bdc8fca52e1ebe31a8d98216428061d3f8956";
    public static readonly Guid ManagedPowerScheme = new("8ed837c8-e85b-48ca-9d8b-f9dc190dc763");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static StartSearchValue D(int value) => StartSearchValue.Dword(value);
    private static StartSearchValue S(string value) => new(RegistryValueKind.String, JsonSerializer.Serialize(value));
    private static PreferenceChange R(string id, string path, string name, StartSearchValue? on, StartSearchValue? off, bool machine = false) => new(new(id, "registry", machine, path, name), on, off);
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public static IReadOnlyList<PreferenceDefinition> All { get; } =
    [
        new("extensions", "explorer-taskbar", "Show file extensions", "Display extensions such as .exe, .zip and .png.", "WPFToggleShowExt", [R("extensions", Advanced, "HideFileExt", D(0), D(1))], RestartExplorer: true),
        new("hidden", "explorer-taskbar", "Show hidden files", "Reveal files marked as hidden; protected system files stay separate.", "WPFToggleHiddenFiles", [R("hidden", Advanced, "Hidden", D(1), D(0))], RestartExplorer: true),
        new("classic-menu", "explorer-taskbar", "Classic right-click menu", "Use the full context menu where supported by your Windows build.", "WPFTweaksRightClickMenu", [new(new("classic-menu", "classic", false, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"), null, null)], RestartExplorer: true, MinBuild: 22000),
        new("alignment", "explorer-taskbar", "Taskbar alignment", "Choose where taskbar icons appear.", "WPFToggleTaskbarAlignment", [R("alignment", Advanced, "TaskbarAl", D(1), D(0))], DefaultOn: true, RestartExplorer: true, MinBuild: 22000, Choices: ["Left", "Center"]),
        new("search-button", "explorer-taskbar", "Show Search button", "Off hides the taskbar search control; On selects the search icon.", "WPFToggleTaskbarSearch", [R("search-button", @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", D(1), D(0))], DefaultOn: true),
        new("task-view", "explorer-taskbar", "Show Task View button", "Keep Task View accessible from the taskbar.", "WPFToggleTaskView", [R("task-view", Advanced, "ShowTaskViewButton", D(1), D(0))], DefaultOn: true),
        new("dark-mode", "appearance-signin", "Windows dark mode", "Changes Windows and supported apps. Studio follows its own theme setting.", "WPFToggleDarkMode", [R("dark-apps", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", D(0), D(1)), R("dark-system", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", D(0), D(1))]),
        new("scrollbars", "appearance-signin", "Always show scrollbars", "Keep scrollbars visible in supported Windows controls.", "WPFToggleScrollbars", [R("scrollbars", @"Control Panel\Accessibility", "DynamicScrollbars", D(0), D(1))]),
        new("verbose", "appearance-signin", "Detailed startup messages", "Show status messages during Windows startup and shutdown.", "WPFToggleVerboseLogon", [R("verbose", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", D(1), D(0), true)]),
        new("login-blur", "appearance-signin", "Blur sign-in background", "Use the acrylic blur effect on the Windows sign-in screen.", "WPFToggleLoginBlur", [R("login-blur", SystemPolicy, "DisableAcrylicBackgroundOnLogon", D(0), D(1), true)], DefaultOn: true),
        new("activity", "privacy-suggestions", "Disable activity publishing", "Stop publishing and uploading activities while preserving clipboard history.", "WPFTweaksActivity", [R("activity-feed", SystemPolicy, "EnableActivityFeed", D(1), null, true), R("activity-publish", SystemPolicy, "PublishUserActivities", D(0), null, true), R("activity-upload", SystemPolicy, "UploadUserActivities", D(0), null, true)]),
        new("location", "privacy-suggestions", "Disable location tracking", "Disables the location service, app location access and automatic map updates. Location-dependent apps are affected.", "WPFTweaksLocation", [new(new("location-service", "service", true, "lfsvc"), D(4), D(3)), R("location-consent", @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", S("Deny"), S("Allow"), true), R("location-sensor", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState", D(0), D(1), true), R("location-maps", @"SYSTEM\Maps", "AutoUpdateEnabled", D(0), D(1), true)]),
        new("consumer", "privacy-suggestions", "Disable consumer features", "Stop promoted app installs and reduce Microsoft Store suggestions on supported Enterprise/Education editions.", "WPFTweaksConsumerFeatures", [R("consumer", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", D(1), null, true)], EnterpriseOnly: true),
        new("hibernation", "power-gaming", "Enable hibernation", "Save the session to disk before powering off. Turning this off also affects Fast Startup and removes the hibernation file.", "WPFTweaksHiber", [new(new("hibernation", "hibernate", true), D(1), D(0)), R("hibernate-reg", @"System\CurrentControlSet\Control\Session Manager\Power", "HibernateEnabled", D(1), D(0), true), R("hibernate-menu", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", D(1), D(0), true)], DefaultOn: true),
        new("game-mode", "power-gaming", "Enable Game Mode", "Use Windows Game Mode when playing games.", "WPFToggleGameMode", [R("game-allow", @"Software\Microsoft\GameBar", "AllowAutoGameMode", D(1), D(0)), R("game-auto", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", D(1), D(0))], DefaultOn: true),
        new("ultimate", "power-gaming", "Ultimate Performance plan", "Create and activate a DevTools copy of WinUtil's Ultimate Performance plan. Desktop only; can increase power use. Off restores the previous plan and removes the DevTools copy.", "WPFAddUltPerf", [new(new("ultimate", "power", true), null, null)], Choices: ["Not enabled", "Enable plan"]),
        new("long-paths", "developer-settings", "Enable long paths", "Allow paths beyond 260 characters in compatible applications. Applications must also support long paths.", "WPFToggleLongPaths", [R("long-paths", @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", D(1), D(0), true)], RestartWindows: true)
    ];
    public static PreferenceResource Resource(string id) => All.SelectMany(d => d.Changes).Single(c => c.Resource.Id == id).Resource;
}
