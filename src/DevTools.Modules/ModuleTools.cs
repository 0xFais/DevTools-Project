using DevTools.Contracts;
using Microsoft.Win32;

namespace DevTools.Modules;

internal static class ModuleTools
{
    public static string Need(ModulePage page, string key)
    {
        var value = page[key].Value.Trim();
        return value.Length == 0 ? throw new ArgumentException(page[key].Label + " is required.") : value;
    }
    public static async Task ChooseFolder(ModulePage p, string key, IModuleContext c)
    {
        if (await c.PickFolderAsync(p[key].Label) is { } path)
            p[key].Value = path;
    }
    public static void SaveFields(ModulePage page, IModuleContext context)
    {
        foreach (var f in page.Fields.Where(f => f.Kind != FieldKind.Secret && f.Kind != FieldKind.ReadOnly))
            context.Save(page.Id + "." + f.Key, f.Value);
    }
    public static ModuleField Field(this ModulePage p, IModuleContext c, string key, string label, string fallback = "", FieldKind kind = FieldKind.Text, string hint = "", params string[] choices)
        => p.Add(key, label, kind == FieldKind.Secret ? "" : c.Get(p.Id + "." + key, fallback), kind, hint, choices);
    public static string ReadRegistry(RegistryKey hive, string path, string name = "")
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(name)?.ToString() ?? "";
    }
    public static void ValidateIdentifier(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Use letters, digits, dot, underscore or hyphen.");
    }
}
