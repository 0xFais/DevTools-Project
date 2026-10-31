using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DevTools.Contracts;

public sealed record ScoopPackage(string Name, string Version, string Bucket, bool Held, string Status,
    string Directory, string ManifestPath, string Description = "", string Homepage = "", string License = "")
{
    public string Identity => Bucket + "/" + Name;
    public string HeldLabel => Held ? "Held" : "";
    public string AvailableVersion => Status.StartsWith("Update:", StringComparison.OrdinalIgnoreCase) ? Status[7..].Trim() : "";
}
public sealed record ScoopBucket(string Name, int Manifests);
public enum ScoopSetupState { Checking, Missing, PathMissing, Ready, Broken, Installing }
public sealed class ScoopWorkspace : ObservableObject
{
    private ScoopSetupState setupState = ScoopSetupState.Checking;
    private string setupMessage = "Checking Scoop installation…";
    private string installLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");
    public ScoopSetupState SetupState { get => setupState; set { if (Set(ref setupState, value)) { Raise(nameof(HasInstallation)); Raise(nameof(ShowInstall)); Raise(nameof(NeedsPath)); Raise(nameof(SetupTitle)); } } }
    public string SetupMessage { get => setupMessage; set => Set(ref setupMessage, value); }
    public string InstallLocation { get => installLocation; set => Set(ref installLocation, value); }
    public bool HasInstallation => SetupState is ScoopSetupState.Ready or ScoopSetupState.PathMissing;
    public bool ShowInstall => SetupState == ScoopSetupState.Missing;
    public bool NeedsPath => SetupState == ScoopSetupState.PathMissing;
    public string SetupTitle => SetupState switch { ScoopSetupState.Ready => "✓ Scoop is ready", ScoopSetupState.PathMissing => "Scoop found — PATH setup needed", ScoopSetupState.Missing => "Scoop is not installed", ScoopSetupState.Broken => "Scoop installation needs attention", ScoopSetupState.Installing => "Installing Scoop…", _ => "Checking Scoop…" };
    private string root = "", filter = "", query = "", summary = "Choose Refresh to detect Scoop.", details = "Select a package to inspect its details.", pathEntries = "", version = "";
    private ScoopPackage? selected;
    private int tab;
    private bool running;
    private string operationStatus = "Ready";
    public bool IsRunning { get => running; set => Set(ref running, value); }
    public string OperationStatus { get => operationStatus; set => Set(ref operationStatus, value); }
    public string Root { get => root; set => Set(ref root, value); }
    public string Filter { get => filter; set { if (Set(ref filter, value)) FilterChanged?.Invoke(); } }
    public string Query { get => query; set => Set(ref query, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string Details { get => details; set => Set(ref details, value); }
    private bool syncingPaths;
    private string? selectedPath;
    private string pathDraft = "", pathMessage = "";
    public string PathMessage { get => pathMessage; set => Set(ref pathMessage, value); }
    public ScoopWorkspace()
    {
        PathItems.CollectionChanged += (_, _) => { if (!syncingPaths) { syncingPaths = true; PathEntries = string.Join(Environment.NewLine, PathItems); syncingPaths = false; } };
        AuthorExecutableItems.CollectionChanged += (_, _) => { if (!syncingAuthorLists) { syncingAuthorLists = true; AuthorExecutable = string.Join(Environment.NewLine, AuthorExecutableItems); syncingAuthorLists = false; } };
        AuthorShortcutItems.CollectionChanged += (_, _) => { if (!syncingAuthorLists) { syncingAuthorLists = true; AuthorShortcuts = string.Join(Environment.NewLine, AuthorShortcutItems); syncingAuthorLists = false; } };
        AuthorPathItems.CollectionChanged += (_, _) => { if (!syncingAuthorLists) { syncingAuthorLists = true; AuthorPaths = string.Join(Environment.NewLine, AuthorPathItems); syncingAuthorLists = false; } };
    }
    public ObservableCollection<string> PathItems { get; } = [];
    public string? SelectedPath { get => selectedPath; set { if (Set(ref selectedPath, value)) PathDraft = value ?? ""; } }
    public string PathDraft { get => pathDraft; set => Set(ref pathDraft, value); }
    public string PathEntries
    {
        get => pathEntries;
        set
        {
            if (!Set(ref pathEntries, value) || syncingPaths) return;
            syncingPaths = true;
            PathItems.Clear();
            foreach (var item in value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) PathItems.Add(item);
            SelectedPath = null;
            syncingPaths = false;
        }
    }
    public string Version { get => version; set => Set(ref version, value); }
    public int Tab { get => tab; set => Set(ref tab, value); }
    public ScoopPackage? Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(HasSelection)); SelectionChanged?.Invoke(); } } }
    public bool HasSelection => Selected is not null;
    public List<ScoopPackage> Selection { get; } = [];
    public List<ScoopPackage> SearchSelection { get; } = [];
    public List<ScoopBucket> BucketSelection { get; } = [];
    public ObservableCollection<ScoopPackage> Installed { get; } = [];
    public ObservableCollection<ScoopPackage> UpdatePackages { get; } = [];
    public List<ScoopPackage> UpdateSelection { get; } = [];
    private string updateCenterMessage = "Refresh the package list to check for updates.";
    public string UpdateCenterMessage { get => updateCenterMessage; set => Set(ref updateCenterMessage, value); }
    public ObservableCollection<ScoopPackage> Results { get; } = [];
    public ObservableCollection<ScoopBucket> Buckets { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<ModuleField> CustomFields { get; } = [];
    public Dictionary<string, ICommand> Commands { get; } = [];
    public ObservableCollection<string> AuthorBuckets { get; } = [];
    public IReadOnlyList<string> AuthorArchitectures { get; } = ["Any", "64bit", "32bit", "arm64"];
    private string newBucketName = "";
    public string NewBucketName { get => newBucketName; set => Set(ref newBucketName, value); }
    private string newBucketLocation = "";
    public string NewBucketLocation { get => newBucketLocation; set => Set(ref newBucketLocation, value); }
    public ObservableCollection<string> EditorManifestNames { get; } = [];
    private string editorBucket = "", editorManifest = "";
    public string EditorBucket { get => editorBucket; set => Set(ref editorBucket, value ?? ""); }
    public string EditorManifest { get => editorManifest; set => Set(ref editorManifest, value ?? ""); }
    private string manifestJson = "", manifestEditorFile = "";
    public string ManifestJson { get => manifestJson; set => Set(ref manifestJson, value); }
    public string ManifestEditorFile { get => manifestEditorFile; set => Set(ref manifestEditorFile, value); }
    private string authorBucket = "";
    public string AuthorBucket { get => authorBucket; set => Set(ref authorBucket, value); }
    private string authorName = "";
    public string AuthorName { get => authorName; set => Set(ref authorName, value); }
    private string authorVersion = "";
    public string AuthorVersion { get => authorVersion; set => Set(ref authorVersion, value); }
    private string authorUrl = "";
    public string AuthorUrl { get => authorUrl; set => Set(ref authorUrl, value); }
    private string authorHash = "";
    public string AuthorHash { get => authorHash; set => Set(ref authorHash, value); }
    private string authorDescription = "";
    public string AuthorDescription { get => authorDescription; set => Set(ref authorDescription, value); }
    private string authorHomepage = "";
    public string AuthorHomepage { get => authorHomepage; set => Set(ref authorHomepage, value); }
    private string authorLicense = "";
    public string AuthorLicense { get => authorLicense; set => Set(ref authorLicense, value); }
    private string authorArchitecture = "Any";
    public string AuthorArchitecture { get => authorArchitecture; set => Set(ref authorArchitecture, value); }
    private string authorExecutable = "";
    private bool syncingAuthorLists;
    private void SyncAuthorList(ObservableCollection<string> list, string value)
    {
        if (syncingAuthorLists) return;
        syncingAuthorLists = true; list.Clear();
        foreach (var item in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) list.Add(item);
        syncingAuthorLists = false;
    }
    public ObservableCollection<string> AuthorExecutableItems { get; } = [];
    public ObservableCollection<string> AuthorShortcutItems { get; } = [];
    public ObservableCollection<string> AuthorPathItems { get; } = [];
    private string? selectedAuthorExecutable, selectedAuthorShortcut, selectedAuthorPath;
    public string? SelectedAuthorExecutable { get => selectedAuthorExecutable; set => Set(ref selectedAuthorExecutable, value); }
    public string? SelectedAuthorShortcut { get => selectedAuthorShortcut; set => Set(ref selectedAuthorShortcut, value); }
    public string? SelectedAuthorPath { get => selectedAuthorPath; set => Set(ref selectedAuthorPath, value); }
    private string authorExecutableDraft = "", authorShortcutExecutableDraft = "", authorShortcutNameDraft = "", authorPathDraft = "", authorListMessage = "";
    public string AuthorExecutableDraft { get => authorExecutableDraft; set => Set(ref authorExecutableDraft, value); }
    public string AuthorShortcutExecutableDraft { get => authorShortcutExecutableDraft; set => Set(ref authorShortcutExecutableDraft, value); }
    public string AuthorShortcutNameDraft { get => authorShortcutNameDraft; set => Set(ref authorShortcutNameDraft, value); }
    public string AuthorPathDraft { get => authorPathDraft; set => Set(ref authorPathDraft, value); }
    public string AuthorListMessage { get => authorListMessage; set => Set(ref authorListMessage, value); }
    public string AuthorExecutable { get => authorExecutable; set { if (Set(ref authorExecutable, value)) SyncAuthorList(AuthorExecutableItems, value); } }
    private string authorShortcuts = "", authorPaths = "";
    public string AuthorShortcuts { get => authorShortcuts; set { if (Set(ref authorShortcuts, value)) SyncAuthorList(AuthorShortcutItems, value); } }
    public string AuthorPaths { get => authorPaths; set { if (Set(ref authorPaths, value)) SyncAuthorList(AuthorPathItems, value); } }
    private string authorShortcut = "";
    public string AuthorShortcut { get => authorShortcut; set => Set(ref authorShortcut, value); }
    public ObservableCollection<string> GitHubReleaseNames { get; } = [];
    public ObservableCollection<string> GitHubAssetNames { get; } = [];
    private string gitHubRepository = "";
    public string GitHubRepository { get => gitHubRepository; set => Set(ref gitHubRepository, value); }
    private string selectedGitHubRelease = "";
    public string SelectedGitHubRelease { get => selectedGitHubRelease; set => Set(ref selectedGitHubRelease, value); }
    private string selectedGitHubAsset = "";
    public string SelectedGitHubAsset { get => selectedGitHubAsset; set => Set(ref selectedGitHubAsset, value); }
    public string BucketName { get; set; } = "";
    public string BucketUrl { get; set; } = "";
    public event Action? FilterChanged;
    public event Action? SelectionChanged;
    public ModuleField Field(string key) => CustomFields.Single(f => f.Key == key);
}
