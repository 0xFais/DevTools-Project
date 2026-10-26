using System.Collections.ObjectModel;

namespace DevTools.Contracts;

public enum StartupEntryType { RegistryValue, StartupFile }
public enum StartupHive { CurrentUser, LocalMachine }
public enum StartupView { Registry32, Registry64 }
public enum StartupRegistryKind { String, ExpandString, Binary, DWord, MultiString, QWord, None, Unknown }
public enum StartupScope { CurrentUser, AllUsers }

public sealed record StartupEntryIdentity(
    StartupEntryType Type,
    StartupHive? Hive = null,
    StartupView? View = null,
    string? ValueName = null,
    StartupRegistryKind? RegistryKind = null,
    string? RawValueJson = null,
    string? Path = null,
    string? StartupRoot = null,
    StartupScope? Scope = null,
    long Length = 0,
    long LastWriteUtcTicks = 0,
    string? Sha256 = null)
{
    public string Id => Type == StartupEntryType.RegistryValue
        ? $"Registry|{Hive}|{View}|{ValueName}"
        : $"File|{Path}";
}

public sealed record StartupAppItem(string Name, string Source, string Scope, string Command, string Status, StartupEntryIdentity Identity)
{
    public string Id => Identity.Id;
    public bool CanRemove => Identity.Type is StartupEntryType.RegistryValue or StartupEntryType.StartupFile;
}

public sealed class StartupAppsWorkspace : ObservableObject
{
    private string summary = "Startup apps have not been checked.";
    private string result = "";
    private string appName = "";
    private string arguments = "";
    private string selectedExecutable = "";
    private bool initialized;
    private StartupAppItem? selectedItem;
    private bool hasSelection;
    public ObservableCollection<StartupAppItem> Items { get; } = [];
    public StartupAppsWorkspace() => Items.CollectionChanged += (_, _) => Raise(nameof(HasItems));
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string Result { get => result; set => Set(ref result, value); }
    public string AppName { get => appName; set => Set(ref appName, value); }
    public string Arguments { get => arguments; set => Set(ref arguments, value); }
    public string SelectedExecutable { get => selectedExecutable; set => Set(ref selectedExecutable, value); }
    public bool IsInitialized { get => initialized; set => Set(ref initialized, value); }
    public bool HasItems => Items.Count > 0;
    public StartupAppItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!Set(ref selectedItem, value)) return;
            HasSelection = value is not null;
            if (RemoveAction is { } action) action.IsEnabled = value?.CanRemove == true;
        }
    }
    public bool HasSelection { get => hasSelection; private set => Set(ref hasSelection, value); }
    public ModuleAction? RefreshAction { get; set; }
    public ModuleAction? BrowseAction { get; set; }
    public ModuleAction? AddAction { get; set; }
    public ModuleAction? RemoveAction { get; set; }
    public ModuleAction? SettingsAction { get; set; }
}
