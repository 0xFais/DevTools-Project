using DevTools.Contracts;
using Microsoft.Win32;

namespace DevTools.Modules.Windows;

public sealed class WindowsUpdatePolicyBackend(IModuleContext context) : IUpdatePolicyBackend
{
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    internal static bool Supports(WindowsEdition edition) => edition.Build >= 10240 && edition.InstallationType == "Client" &&
        (ActivationCatalog.Keys.ContainsKey(edition.Id) || edition.Id is "EnterpriseS" or "EnterpriseSN" or "IoTEnterprise" or "IoTEnterpriseS");
    public UpdatePolicyState Read()
    {
        var edition = new WindowsActivationBackend(context).ReadEdition();
        using var key = Registry.LocalMachine.OpenSubKey(PolicyPath);
        var raw = key?.GetValue("NoAutoUpdate");
        if (raw is not null && (key!.GetValueKind("NoAutoUpdate") != RegistryValueKind.DWord || raw is not int n || n is not (0 or 1)))
            throw new InvalidOperationException("NoAutoUpdate has an unsupported type or value. It was not changed.");
        using var mdm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
        var conflict = mdm?.GetValue("IgnoreWindowsUpdateGroupPolicies") is int ignore && ignore == 1 ? "Device management is configured to ignore Windows Update Group Policy." :
            mdm?.GetValue("AllowAutoUpdate_ProviderSet") is int provider && provider == 1 ? "Automatic updates are configured by device management. Change them through your organization." : null;
        return new(Supports(edition), edition.Display, raw is int value ? value : null, conflict);
    }
    public void Write(int? value)
    {
        if (value is not (null or 0 or 1)) throw new ArgumentException("Unsupported policy value.");
        if (value is null) { using var existing = Registry.LocalMachine.OpenSubKey(PolicyPath, true); existing?.DeleteValue("NoAutoUpdate", false); }
        else { using var key = Registry.LocalMachine.CreateSubKey(PolicyPath, true); key.SetValue("NoAutoUpdate", value.Value, RegistryValueKind.DWord); }
        // Do not delete the policy key: unrelated settings belong to other tools.
    }
}
