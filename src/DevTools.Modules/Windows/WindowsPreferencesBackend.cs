using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevTools.Infrastructure;
using Microsoft.Win32;

namespace DevTools.Modules.Windows;

public sealed record PreferencesPlatform(StartSearchPlatform System, bool Desktop);
public sealed record PreferencePowerState(bool Exists, Guid Active);
public sealed record PreferenceClassicState(bool ClassExists, bool HandlerExists, StartSearchValue? DefaultValue);
public interface IWindowsPreferencesBackend
{
    PreferencesPlatform Platform();
    StartSearchValue? Read(PreferenceResource resource);
    Task WriteAsync(PreferenceResource resource, StartSearchValue? value);
    void Notify();
    Task RestartExplorerAsync(CancellationToken ct);
}

public sealed class WindowsPreferencesBackend : IWindowsPreferencesBackend
{
    private readonly StartSearchRegistryBackend registry = new();
    public PreferencesPlatform Platform() => new(registry.Platform(), GetSystemPowerStatus(out var status) && status.BatteryFlag == 128);
    public StartSearchValue? Read(PreferenceResource resource)
    {
        return resource.Kind switch
        {
            "registry" => registry.Read(new(resource.Id, resource.Path, resource.Name, false, resource.Machine)),
            "service" => ReadService(resource.Path),
            "hibernate" => StartSearchValue.Dword(HibernationEnabled() ? 1 : 0),
            "power" => PowerValue(ReadPower()),
            "classic" => ClassicValue(ReadClassic(resource)),
            _ => throw new InvalidDataException("Unknown Windows preference resource.")
        };
    }
    public async Task WriteAsync(PreferenceResource resource, StartSearchValue? value)
    {
        switch (resource.Kind)
        {
            case "registry": registry.Write(new(resource.Id, resource.Path, resource.Name, false, resource.Machine), value); break;
            case "service": WriteService(resource.Path, JsonSerializer.Deserialize<uint>(value!.Json)); break;
            case "hibernate":
                // Complete this short Windows operation before honoring cancellation in the transaction.
                (await ProcessRunner.RunAsync(PowerCfg, ["/hibernate", value == StartSearchValue.Dword(1) ? "on" : "off"], CancellationToken.None)).EnsureSuccess();
                break;
            case "power": await WritePowerAsync(PowerState(value!)); break;
            case "classic": WriteClassic(resource, ClassicState(value!)); break;
            default: throw new InvalidDataException("Unknown Windows preference resource.");
        }
    }
    private static string PowerCfg => Path.Combine(Environment.SystemDirectory, "powercfg.exe");
    public static StartSearchValue PowerValue(PreferencePowerState state) => new(RegistryValueKind.String, JsonSerializer.Serialize(state));
    public static PreferencePowerState PowerState(StartSearchValue value) => JsonSerializer.Deserialize<PreferencePowerState>(value.Json) ?? throw new InvalidDataException("Missing saved power state.");
    public static StartSearchValue ClassicValue(PreferenceClassicState state) => new(RegistryValueKind.String, JsonSerializer.Serialize(state));
    public static PreferenceClassicState ClassicState(StartSearchValue value) => JsonSerializer.Deserialize<PreferenceClassicState>(value.Json) ?? throw new InvalidDataException("Missing saved Explorer menu state.");
    private PreferenceClassicState ReadClassic(PreferenceResource resource)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(resource.Path);
        using var handler = parent?.OpenSubKey("InprocServer32");
        return new(parent is not null, handler is not null, registry.Read(new(resource.Id, resource.Path + @"\InprocServer32", "", false)));
    }
    private void WriteClassic(PreferenceResource resource, PreferenceClassicState state)
    {
        if (state.HandlerExists && !state.ClassExists) throw new InvalidDataException("An Explorer handler requires its class key.");
        if (!state.HandlerExists)
        {
            using var parent = Registry.CurrentUser.OpenSubKey(resource.Path, true);
            using (var handler = parent?.OpenSubKey("InprocServer32"))
            {
                if (handler is not null && (handler.SubKeyCount > 0 || handler.GetValueNames().Any(name => name != ""))) throw new IOException("The Explorer menu key contains additional settings; it was preserved.");
                if (!state.ClassExists && parent is not null && (parent.ValueCount > 0 || parent.GetSubKeyNames().Any(name => !name.Equals("InprocServer32", StringComparison.OrdinalIgnoreCase)))) throw new IOException("The Explorer class contains additional settings; it was preserved.");
            }
            parent?.DeleteSubKey("InprocServer32", false);
            if (!state.ClassExists) Registry.CurrentUser.DeleteSubKey(resource.Path, false);
            else if (parent is null) { using var created = Registry.CurrentUser.CreateSubKey(resource.Path); }
        }
        else
        {
            using var handler = Registry.CurrentUser.CreateSubKey(resource.Path + @"\InprocServer32");
            registry.Write(new(resource.Id, resource.Path + @"\InprocServer32", "", false), state.DefaultValue);
        }
    }
    private static bool HibernationEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return key?.GetValue("HibernateEnabled") is int value && value != 0;
    }
    private static StartSearchValue? ReadService(string name)
    {
        // Start is the SCM's persisted startup mode. No running-state change is made by WinUtil's location tweak.
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key?.GetValue("Start") is int value ? StartSearchValue.Dword(value) : null;
    }
    private static void WriteService(string name, uint start)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, name, 2);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!ChangeServiceConfig(service, uint.MaxValue, start, uint.MaxValue, null, null, 0, null, null, null, null)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static PreferencePowerState ReadPower()
    {
        Error(PowerGetActiveScheme(0, out var pointer));
        Guid active;
        try { active = Marshal.PtrToStructure<Guid>(pointer); } finally { LocalFree(pointer); }
        var buffer = Marshal.AllocHGlobal(16);
        var exists = false;
        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = 16;
                var result = PowerEnumerate(0, 0, 0, 16, index, buffer, ref size);
                if (result == 259) break;
                Error(result);
                if (Marshal.PtrToStructure<Guid>(buffer) == WindowsPreferenceDefinitions.ManagedPowerScheme) exists = true;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new(exists, active);
    }
    private static async Task WritePowerAsync(PreferencePowerState state)
    {
        if (!state.Exists && state.Active == WindowsPreferenceDefinitions.ManagedPowerScheme) throw new InvalidDataException("A removed power plan cannot stay active.");
        var before = ReadPower();
        var plan = WindowsPreferenceDefinitions.ManagedPowerScheme;
        try
        {
            if (state.Exists && !before.Exists)
            {
                (await ProcessRunner.RunAsync(PowerCfg, ["/duplicatescheme", WindowsPreferenceDefinitions.UltimateTemplate.ToString(), plan.ToString()], CancellationToken.None)).EnsureSuccess();
                (await ProcessRunner.RunAsync(PowerCfg, ["/changename", plan.ToString(), "Ultimate Performance (DevTools)"], CancellationToken.None)).EnsureSuccess();
            }
            var active = state.Active;
            Error(PowerSetActiveScheme(0, ref active));
            if (!state.Exists && ReadPower().Exists) Error(PowerDeleteScheme(0, ref plan));
        }
        catch (Exception failure)
        {
            try
            {
                var active = before.Active; Error(PowerSetActiveScheme(0, ref active));
                if (!before.Exists && ReadPower().Exists) Error(PowerDeleteScheme(0, ref plan));
            }
            catch (Exception rollback) { throw new AggregateException("Power plan change and recovery failed; the restore backup was kept.", failure, rollback); }
            throw;
        }
    }
    private static void Error(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code)); }
    public void Notify()
    {
        SendMessageTimeout(new nint(0xffff), 0x1a, 0, "ImmersiveColorSet", 2, 2000, out _);
        SendMessageTimeout(new nint(0xffff), 0x1a, 0, @"Control Panel\Accessibility", 2, 2000, out _);
        SHChangeNotify(0x08000000, 0, 0, 0);
    }
    public Task RestartExplorerAsync(CancellationToken ct) => registry.RestartExplorerAsync(ct);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error, string? path, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(nint root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(nint root, nint scheme, nint subgroup, uint access, uint index, nint buffer, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nint wparam, string lparam, uint flags, uint timeout, out nint result);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
