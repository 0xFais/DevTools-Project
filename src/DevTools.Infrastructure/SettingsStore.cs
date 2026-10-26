using System.Text.Json;
using System.Runtime.InteropServices;

namespace DevTools.Infrastructure;

public sealed class SettingsStore
{
    private readonly string path;
    private readonly Dictionary<string, string> values;
    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
        values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
    }
    public string Get(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
    public void Save(string key, string value)
    {
        values[key] = value;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                sections.TryAdd(section, new(StringComparer.OrdinalIgnoreCase));
            }
            else if (line.IndexOf('=') is var n && n > 0 && section != "")
                sections[section][line[..n].Trim()] = line[(n + 1)..].Trim();
        }
        return sections;
    }
}
