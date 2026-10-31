using System.Collections.ObjectModel;
using DevTools.Contracts;

namespace DevTools.App;

public sealed class NavigationNode : ObservableObject
{
    private bool expanded;
    private bool selected;
    private bool visible = true;
    private bool temporaryExpansion;
    private readonly Action<string, bool>? saveExpansion;
    public string Id { get; }
    public string Title { get; }
    public string Icon => Page?.Icon ?? "";
    public ModulePage? Page { get; }
    public bool IsGroup => Page is null;
    public ObservableCollection<NavigationNode> Children { get; } = [];
    public bool IsExpanded { get => expanded; set { if (Set(ref expanded, value) && !temporaryExpansion) saveExpansion?.Invoke(Id, value); } }
    public bool IsVisible { get => visible; set => Set(ref visible, value); }
    public void SetTemporaryExpansion(bool enabled) => temporaryExpansion = enabled;
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public NavigationNode(string id, string title, ModulePage? page = null, bool expanded = true, Action<string, bool>? saveExpansion = null)
        => (Id, Title, Page, this.expanded, this.saveExpansion) = (id, title, page, expanded, saveExpansion);
    public bool Reveal(string pageId)
    {
        IsSelected = Page?.Id == pageId;
        var found = IsSelected;
        foreach (var child in Children) found = child.Reveal(pageId) || found;
        if (found && IsGroup) IsExpanded = true;
        return found;
    }
}
