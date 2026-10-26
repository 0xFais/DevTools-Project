using DevTools.Contracts;
using DevTools.Modules.Integrations;
using DevTools.Modules.Windows;

namespace DevTools.Modules;

public static class BuiltInModules
{
    public static IEnumerable<IDevToolsModule> Create() => [new StartSearchModule(), .. WindowsPreferencesModule.Pages.Select(p => new WindowsPreferencesModule(p.Id)), new Scoop.ScoopModule(), new Development.BinaryInventoryModule(), new Development.EnvironmentPathModule(), new Development.SdkCatalogModule(), new ContextMenuModule(), new LinkHandlersModule(), new ActivationModule(), new WindowsUpdatesModule(), new UserFoldersModule(), new SettingsModule()];
}
