using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DevTools.Contracts;

public sealed record EnvironmentVariableInfo(string Name, string ProcessValue, string UserValue, string MachineValue, string Source)
{
    public string DisplayValue => ProcessValue.Length == 0 ? "Not set" : ProcessValue;
}

public sealed record PathEntryInfo(int Index, string Path, string Scope, string Status, string DuplicateOf)
{
    public string DisplayStatus => DuplicateOf.Length > 0 ? "Duplicate of #" + DuplicateOf : Status;
}

public sealed class EnvironmentPathWorkspace : ObservableObject
{
    private string search = "";
    private string executableQuery = "gcc.exe";
    private string resolvedExecutable = "No matching executable found in process PATH.";
    private string summary = "Refresh to inspect environment variables and PATH.";
    private EnvironmentVariableInfo? selectedVariable;

    public ObservableCollection<EnvironmentVariableInfo> Variables { get; } = [];
    public ObservableCollection<EnvironmentVariableInfo> VisibleVariables { get; } = [];
    public ObservableCollection<PathEntryInfo> PathEntries { get; } = [];
    public Dictionary<string, ICommand> Commands { get; } = [];
    public string Search { get => search; set { if (Set(ref search, value)) FilterChanged?.Invoke(); } }
    public string ExecutableQuery { get => executableQuery; set { if (Set(ref executableQuery, value)) QueryChanged?.Invoke(); } }
    public string ResolvedExecutable { get => resolvedExecutable; set => Set(ref resolvedExecutable, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public EnvironmentVariableInfo? SelectedVariable { get => selectedVariable; set => Set(ref selectedVariable, value); }
    public event Action? FilterChanged;
    public event Action? QueryChanged;
}
