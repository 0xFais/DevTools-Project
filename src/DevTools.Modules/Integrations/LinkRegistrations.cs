using DevTools.Contracts;
using DevTools.Infrastructure;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Integrations;

public sealed class LinkRegistrations(IIntegrationRegistry registry)
{
    public const string Classes = @"Software\Classes\";
    public string UserChoice(string scheme) => registry.Read(IntegrationHive.User, @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice", "ProgId") ?? "";
    public string UserCommand(string scheme) => registry.Read(IntegrationHive.User, Classes + scheme + @"\shell\open\command") ?? "";
    private string EffectiveCommand(string scheme)
    {
        var choice = UserChoice(scheme);
        var id = choice != "" ? choice : scheme;
        return registry.Read(IntegrationHive.User, Classes + id + @"\shell\open\command") ?? registry.Read(IntegrationHive.Machine, Classes + id + @"\shell\open\command") ?? "";
    }
    public IntegrationItem Inspect(LinkProfile app, string configuredPath)
    {
        var user = UserCommand(app.Scheme);
        var machine = registry.Read(IntegrationHive.Machine, Classes + app.Scheme + @"\shell\open\command") ?? "";
        var active = EffectiveCommand(app.Scheme);
        var matching = new[] { active, user, machine }.FirstOrDefault(command => IntegrationCommands.Matches(command, app.Exe, configuredPath)) ?? "";
        var path = IntegrationCommands.Executable(matching);
        if (path == "")
            path = configuredPath;
        var protectedDefault = UserChoice(app.Scheme) != "";
        var notice = protectedDefault ? "Windows controls this protocol's default. Use Windows default apps to change it." : "Registering a handler saves the previous per-user command so it can be restored.";
        if (matching != "" && matching != active)
            notice = "Registered, but another Windows default currently takes precedence. " + notice;
        return new(app.Id, app.Name, path, matching != "", File.Exists(path), app.Scheme + ":", true, app.Id.StartsWith("custom_"), notice);
    }
    public IEnumerable<IntegrationItem> OtherRegistrations(IEnumerable<LinkProfile> apps)
    {
        var catalog = apps.ToArray();
        var schemes = registry.SubKeys(IntegrationHive.User, Classes.TrimEnd('\\')).Concat(registry.SubKeys(IntegrationHive.Machine, Classes.TrimEnd('\\'))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in schemes)
        {
            if (!ValidScheme(scheme) || (registry.Read(IntegrationHive.User, Classes + scheme, "URL Protocol") is null && registry.Read(IntegrationHive.Machine, Classes + scheme, "URL Protocol") is null))
                continue;
            var command = EffectiveCommand(scheme);
            var executable = IntegrationCommands.Executable(command);
            if (executable == "" || catalog.Any(a => a.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) && IntegrationCommands.Matches(command, a.Exe)))
                continue;
            var name = Path.GetFileNameWithoutExtension(executable);
            yield return new("external:" + scheme, name, executable, true, File.Exists(executable), scheme + ":", false,
                Notice: "Existing protocol registration detected outside the profile catalog. Manage it in Windows default apps or create a custom profile.");
        }
    }
    public static bool ValidScheme(string scheme) => Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]{0,60}$", RegexOptions.IgnoreCase);
    public void Register(string scheme, string executable, IModuleContext c)
    {
        if (!ValidScheme(scheme))
            throw new ArgumentException("Invalid URL scheme.");
        IntegrationCommands.ValidateExecutable(executable);
        if (UserChoice(scheme) != "")
            throw new InvalidOperationException("Windows-protected defaults must be changed in Settings.");
        var path = Classes + scheme;
        var current = UserCommand(scheme);
        if (c.Get("links.owned." + scheme) == "" || current != c.Get("links.owned." + scheme))
        {
            c.Save("links.previous." + scheme, current);
            c.Save("links.label." + scheme, registry.Read(IntegrationHive.User, path) ?? "");
            c.Save("links.labelExists." + scheme, registry.Read(IntegrationHive.User, path) is null ? "false" : "true");
            c.Save("links.protocol." + scheme, registry.Read(IntegrationHive.User, path, "URL Protocol") is null ? "false" : "true");
        }
        var command = '"' + executable + "\" \"%1\"";
        registry.Write(path, "", "URL:" + scheme);
        registry.Write(path, "URL Protocol", "");
        registry.Write(path + @"\shell\open\command", "", command);
        c.Save("links.owned." + scheme, command);
    }
    public void Restore(string scheme, IModuleContext c)
    {
        if (!ValidScheme(scheme))
            throw new ArgumentException("Invalid URL scheme.");
        var owned = c.Get("links.owned." + scheme);
        if (owned == "" || UserCommand(scheme) != owned)
            throw new InvalidOperationException("The current per-user command is not owned by DevTools; nothing was changed.");
        var path = Classes + scheme;
        var previous = c.Get("links.previous." + scheme);
        if (previous == "")
            registry.DeleteValue(path + @"\shell\open\command", "");
        else
            registry.Write(path + @"\shell\open\command", "", previous);
        if (c.Get("links.labelExists." + scheme, "true") == "false")
            registry.DeleteValue(path, "");
        else
            registry.Write(path, "", c.Get("links.label." + scheme));
        if (c.Get("links.protocol." + scheme) == "false")
            registry.DeleteValue(path, "URL Protocol");
        c.Save("links.owned." + scheme, "");
    }
}
