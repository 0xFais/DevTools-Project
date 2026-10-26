using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace DevTools.Infrastructure;

public enum IntegrationHive
{
    User, Machine
}
public interface IIntegrationRegistry
{
    string? Read(IntegrationHive hive, string path, string name = "");
    bool Exists(IntegrationHive hive, string path);
    string[] SubKeys(IntegrationHive hive, string path);
    void Write(string path, string name, string value);
    void DeleteTree(string path);
    void DeleteValue(string path, string name);
}

public sealed class IntegrationRegistry : IIntegrationRegistry
{
    private static RegistryKey Root(IntegrationHive hive) => hive == IntegrationHive.User ? Registry.CurrentUser : Registry.LocalMachine;
    public string? Read(IntegrationHive hive, string path, string name = "")
    {
        using var key = Root(hive).OpenSubKey(path);
        return key?.GetValue(name)?.ToString();
    }
    public bool Exists(IntegrationHive hive, string path)
    {
        using var key = Root(hive).OpenSubKey(path);
        return key is not null;
    }
    public string[] SubKeys(IntegrationHive hive, string path)
    {
        using var key = Root(hive).OpenSubKey(path);
        return key?.GetSubKeyNames() ?? [];
    }
    public void Write(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value);
    }
    public void DeleteTree(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, false);
    public void DeleteValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }
}

public static class IntegrationCommands
{
    public static string Executable(string command)
    {
        var match = Regex.Match(command.Trim(), "^\"([^\"]+\\.exe)\"|^(.+?\\.exe)(?:\\s|$)", RegexOptions.IgnoreCase);
        return match.Success ? Environment.ExpandEnvironmentVariables(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "";
    }
    public static bool Matches(string command, string executable, string configuredPath = "")
    {
        var path = Executable(command);
        return path != "" && (Path.GetFileName(path).Equals(executable, StringComparison.OrdinalIgnoreCase)
            || (configuredPath != "" && path.Equals(configuredPath, StringComparison.OrdinalIgnoreCase)));
    }
    public static void ValidateExecutable(string executable)
    {
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executable.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("Choose a valid application executable.");
    }
}
