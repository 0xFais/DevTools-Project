using DevTools.Contracts;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Development;

public sealed class PostgreSqlModule : IDevToolsModule
{
    public string Id => "postgresql";
    public ModulePage CreatePage(IModuleContext c)
    {
        var p = new ModulePage(Id, "PostgreSQL", "Register and manage your local PostgreSQL Windows service.", "Development", "\uE753");
        p.Field(c, "exe", "pg_ctl.exe", Path.Combine(c.Get("settings.root"), @"apps\postgresql\current\bin\pg_ctl.exe"));
        p.Field(c, "data", "Data directory", Path.Combine(c.Get("settings.root"), @"apps\postgresql\current\data"));
        p.Field(c, "service", "Service name", "PostgreSQL-18");
        p.Action("Inspect service", async ct => { ModuleTools.ValidateIdentifier(ModuleTools.Need(p, "service")); await c.ShowReportAsync("PostgreSQL service", (await c.RunAsync("sc.exe", ["query", p["service"].Value], ct)).Output); }, c);
        p.Action("Detect registered service", ct =>
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            var name = services?.GetSubKeyNames().FirstOrDefault(n => n.Contains("postgres", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No PostgreSQL service found.");
            p["service"].Value = name;
            var image = ModuleTools.ReadRegistry(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + name, "ImagePath");
            var exe = Regex.Match(image, "^\"?([^\"]*?pg_ctl\\.exe)", RegexOptions.IgnoreCase);
            if (exe.Success)
                p["exe"].Value = exe.Groups[1].Value;
            var data = Regex.Match(image, "-D\\s+(?:\"([^\"]+)\"|(\\S+))");
            if (data.Success)
                p["data"].Value = data.Groups[1].Success ? data.Groups[1].Value : data.Groups[2].Value;
            return Task.CompletedTask;
        }, c);
        p.Action("Choose pg_ctl", async ct => { if (await c.PickFileAsync("PostgreSQL service executable", "pg_ctl|pg_ctl.exe") is { } path) p["exe"].Value = path; }, c);
        p.Action("Choose data directory", ct => ModuleTools.ChooseFolder(p, "data", c), c);
        foreach (var action in new[] { "Register + start", "Start", "Stop", "Unregister" })
            p.Action(action, async ct =>
            {
                var name = ModuleTools.Need(p, "service");
                ModuleTools.ValidateIdentifier(name);
                ModuleTools.SaveFields(p, c);
                if (!await c.ConfirmAsync(action + " PostgreSQL", "Apply this operation to the service " + name + "?"))
                    return;
                if (action == "Register + start")
                {
                    var exe = ModuleTools.Need(p, "exe");
                    var data = ModuleTools.Need(p, "data");
                    if (!File.Exists(exe) || !File.Exists(Path.Combine(data, "PG_VERSION")))
                        throw new ArgumentException("A valid pg_ctl.exe and initialized data directory are required.");
                    (await c.RunAsync(exe, ["register", "-N", name, "-D", data, "-S", "auto"], ct)).EnsureSuccess();
                    (await c.RunAsync("sc.exe", ["start", name], ct)).EnsureSuccess();
                }
                else if (action == "Unregister")
                {
                    var stop = await c.RunAsync("sc.exe", ["stop", name], ct);
                    c.Log(p.Title, stop.Output);
                    (await c.RunAsync(ModuleTools.Need(p, "exe"), ["unregister", "-N", name], ct)).EnsureSuccess();
                }
                else
                    (await c.RunAsync("sc.exe", [action.ToLowerInvariant(), name], ct)).EnsureSuccess();
            }, c, action == "Register + start");
        return p;
    }
}
