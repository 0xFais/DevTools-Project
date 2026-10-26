using DevTools.Contracts;

namespace DevTools.SampleExtension;

public sealed class SystemInfoModule : IDevToolsModule
{
    public string Id => "sample.system-info";
    public ModulePage CreatePage(IModuleContext context)
    {
        var page = new ModulePage(Id, "System information", "A small, independently installed extension.", "Extensions", "\uE946");
        page.Add("machine", "Computer", Environment.MachineName, FieldKind.ReadOnly);
        page.Add("os", "Operating system", Environment.OSVersion.ToString(), FieldKind.ReadOnly);
        page.Action("Show environment summary", async ct => await context.ShowReportAsync("System information", $"Machine: {Environment.MachineName}\nOS: {Environment.OSVersion}\nProcessor count: {Environment.ProcessorCount}"), context, true);
        return page;
    }
}
