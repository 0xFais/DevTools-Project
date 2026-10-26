using DevTools.Contracts;
using Microsoft.Win32;

namespace DevTools.Modules.Windows;

public sealed class WindowsUpdatePauseBackend(IModuleContext context) : IUpdatePauseBackend
{
    private static string PathFor(string name) => name.StartsWith("UX/") ? @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings" : @"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings";
    public UpdatePauseState Read()
    {
        var edition = new WindowsActivationBackend(context).ReadEdition(); var values = new Dictionary<string, PauseValue?>();
        foreach (var name in UpdatePauseController.Names)
        {
            using var key = Registry.LocalMachine.OpenSubKey(PathFor(name)); var valueName = name.Split('/')[1]; var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = value is null ? null : key!.GetValueKind(valueName) switch { RegistryValueKind.String => PauseValue.String((string)value), RegistryValueKind.DWord => PauseValue.Dword((int)value), _ => throw new InvalidOperationException("Unsupported registry type for " + valueName + "; it was not changed.") };
        }
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        using var mdm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
        var blocked = policy?.GetValue("SetDisablePauseUXAccess") is int gp && gp == 1 || mdm?.GetValue("SetDisablePauseUXAccess") is int managed && managed == 1;
        return new(edition.Build >= 10240 && edition.InstallationType == "Client", edition.Display, values, blocked ? "Pausing updates is disabled by management policy." : null);
    }
    public void Write(IReadOnlyDictionary<string, PauseValue?> values)
    {
        if (!UpdatePauseStore.ValidValues(values)) throw new InvalidDataException("Only known Windows pause values may be changed.");
        // Limit first, then timestamps, then status. Do not touch services or unrelated values.
        foreach (var name in UpdatePauseController.Names)
        {
            var value = values[name]; var valueName = name.Split('/')[1];
            if (value is null) { using var existing = Registry.LocalMachine.OpenSubKey(PathFor(name), true); existing?.DeleteValue(valueName, false); }
            else { using var key = Registry.LocalMachine.CreateSubKey(PathFor(name), true); if (value.Kind == "DWord") key.SetValue(valueName, value.Number!.Value, RegistryValueKind.DWord); else key.SetValue(valueName, value.Text!, RegistryValueKind.String); }
        }
    }
}
