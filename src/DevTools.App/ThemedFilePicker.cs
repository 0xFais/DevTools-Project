using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevTools.App;

/// <summary>App-themed file/folder selection without depending on Windows' separate appearance setting.</summary>
public sealed class ThemedFilePicker : ThemedWindow
{
    private readonly bool foldersOnly;
    private readonly TextBox location = new(), filename = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox entries = new();
    private readonly ComboBox types = new() { MinWidth = 160 };
    private readonly List<(string Label, string Pattern)> filters = [];
    private string current = "";
    public string? SelectedPath { get; private set; }
    private sealed record Entry(string Path, bool Folder)
    {
        public string Label => (Folder ? "▸  " : "    ") + System.IO.Path.GetFileName(Path);
    }
    public ThemedFilePicker(string title, bool foldersOnly, string filter = "All files|*.*", string? initialDirectory = null)
    {
        Title = title; Width = 820; Height = 590; MinWidth = 480; MinHeight = 350; this.foldersOnly = foldersOnly;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 21, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var navigation = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var up = new Button { Content = "Up", Margin = new Thickness(0, 0, 8, 0) };
        up.Click += (_, _) => Navigate(Directory.GetParent(current)?.FullName ?? current);
        DockPanel.SetDock(up, Dock.Left); navigation.Children.Add(up);
        var drives = new ComboBox { Width = 95, Margin = new Thickness(0, 0, 8, 0), ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToArray() };
        drives.SelectionChanged += (_, _) => { if (drives.SelectedItem is string drive) Navigate(drive); };
        DockPanel.SetDock(drives, Dock.Left); navigation.Children.Add(drives);
        var go = new Button { Content = "Go", Margin = new Thickness(8, 0, 0, 0) }; go.Click += (_, _) => Navigate(location.Text);
        DockPanel.SetDock(go, Dock.Right); navigation.Children.Add(go); navigation.Children.Add(location);
        location.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(location.Text); e.Handled = true; } };
        DockPanel.SetDock(navigation, Dock.Top); panel.Children.Add(navigation);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (!foldersOnly)
        {
            var tokens = filter.Split('|');
            for (var i = 0; i + 1 < tokens.Length; i += 2) filters.Add((tokens[i], tokens[i + 1]));
            if (filters.Count == 0) filters.Add(("All files", "*.*"));
            types.ItemsSource = filters.Select(f => f.Label).ToArray(); types.SelectedIndex = 0;
            types.SelectionChanged += (_, _) => Navigate(current);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(types, Dock.Right); row.Children.Add(types); filename.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(filename); bottom.Children.Add(row);
            filename.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        }
        bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var accept = new Button { Content = foldersOnly ? "Select folder" : "Open", IsDefault = true }; accept.Click += (_, _) => Accept();
        buttons.Children.Add(accept); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) }); bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        entries.DisplayMemberPath = nameof(Entry.Label);
        entries.SetResourceReference(BackgroundProperty, "SidebarBackground"); entries.SetResourceReference(ForegroundProperty, "TextBrush"); entries.SetResourceReference(BorderBrushProperty, "LineBrush");
        entries.SelectionChanged += (_, _) => { if (entries.SelectedItem is Entry { Folder: false } item) filename.Text = Path.GetFileName(item.Path); };
        entries.MouseDoubleClick += (_, _) => { if (entries.SelectedItem is Entry item) { if (item.Folder) Navigate(item.Path); else Accept(); } };
        entries.KeyDown += (_, e) => { if (e.Key == Key.Enter && entries.SelectedItem is Entry item) { if (item.Folder) Navigate(item.Path); else Accept(); e.Handled = true; } };
        panel.Children.Add(entries); Content = panel;
        Navigate(initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
    public void Navigate(string path)
    {
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Folder does not exist.");
            var rows = Directory.EnumerateDirectories(path).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(p => new Entry(p, true)).ToList();
            if (!foldersOnly)
            {
                var patterns = filters[Math.Max(0, types.SelectedIndex)].Pattern.Split(';');
                rows.AddRange(Directory.EnumerateFiles(path).Where(p => patterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(p)))).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(p => new Entry(p, false)));
            }
            current = path; location.Text = path; entries.ItemsSource = rows; filename.Clear(); status.Text = foldersOnly ? "Select a folder, or use the folder currently open." : "Select a file or enter its name.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = e.Message; }
    }
    private void Accept()
    {
        try
        {
            var selected = foldersOnly ? entries.SelectedItem is Entry { Folder: true } folder ? folder.Path : current : Path.GetFullPath(Path.Combine(current, filename.Text));
            if (foldersOnly ? !Directory.Exists(selected) : !File.Exists(selected)) throw new IOException("Choose an existing " + (foldersOnly ? "folder." : "file."));
            SelectedPath = selected; DialogResult = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { status.Text = e.Message; }
    }
}
