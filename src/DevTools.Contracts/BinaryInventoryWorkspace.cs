using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DevTools.Contracts;

public sealed record DeveloperBinary(string Name, string Executable, string ScoopPackage, string Category, string Description)
{
    public string Status { get; set; } = "Not installed through Scoop";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public string Version { get; set; } = "";
    public string Details { get; set; } = "";
    public string DisplayStatus => Status;
}

public sealed class BinaryInventoryWorkspace : ObservableObject
{
    private string search = "";
    private DeveloperBinary? selected;
    private string summary = "Scoop packages only · choose Scan to refresh.";
    private string scoopMessage = "";
    public ObservableCollection<DeveloperBinary> Items { get; } = [];
    public ObservableCollection<DeveloperBinary> VisibleItems { get; } = [];
    public Dictionary<string, ICommand> Commands { get; } = [];
    public string Search { get => search; set { if (Set(ref search, value)) FilterChanged?.Invoke(); } }
    public DeveloperBinary? Selected { get => selected; set { if (Set(ref selected, value)) Raise(nameof(HasSelection)); } }
    public bool HasSelection => Selected is not null;
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string ScoopMessage { get => scoopMessage; set => Set(ref scoopMessage, value); }
    public event Action? FilterChanged;
}
