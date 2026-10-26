using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DevTools.Contracts;

public sealed record SdkEcosystemInfo(string Name, string Version, string Location, string Description)
{
    public string DisplayVersion => Version.Length == 0 ? "Not detected" : Version;
}

public sealed record InstalledSdkInfo(string Version, string Architecture, string Directory);
public sealed record InstalledRuntimeInfo(string Name, string Version, string Directory);

public sealed class SdkCatalogWorkspace : ObservableObject
{
    private SdkEcosystemInfo? selectedEcosystem;
    private string hostVersion = "Not detected";
    private string architecture = "—";
    private string rid = "—";
    private string basePath = "—";
    private string dotnetPath = "—";
    private string summary = "Refresh to inspect installed SDKs and runtimes.";
    private string sdkDetails = "No SDKs detected.";
    private string runtimeDetails = "No runtimes detected.";
    private string hostSummary = "Host not detected";

    public ObservableCollection<SdkEcosystemInfo> Ecosystems { get; } = [];
    public ObservableCollection<InstalledSdkInfo> DotnetSdks { get; } = [];
    public ObservableCollection<InstalledRuntimeInfo> DotnetRuntimes { get; } = [];
    public Dictionary<string, ICommand> Commands { get; } = [];
    public SdkEcosystemInfo? SelectedEcosystem
    {
        get => selectedEcosystem;
        set
        {
            if (Set(ref selectedEcosystem, value))
                Raise(nameof(IsDotNetSelected));
        }
    }
    public bool IsDotNetSelected => SelectedEcosystem?.Name == ".NET";
    public string HostVersion { get => hostVersion; set => Set(ref hostVersion, value); }
    public string Architecture { get => architecture; set => Set(ref architecture, value); }
    public string Rid { get => rid; set => Set(ref rid, value); }
    public string BasePath { get => basePath; set => Set(ref basePath, value); }
    public string DotnetPath { get => dotnetPath; set => Set(ref dotnetPath, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string SdkDetails { get => sdkDetails; set => Set(ref sdkDetails, value); }
    public string RuntimeDetails { get => runtimeDetails; set => Set(ref runtimeDetails, value); }
    public string HostSummary { get => hostSummary; set => Set(ref hostSummary, value); }
}
