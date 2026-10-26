# Developing an extension

The sample project under `extensions/DevTools.SampleExtension` is the starting point.
It builds separately and does not require editing the host.

1. Create a .NET 10 Windows class library.
2. Reference `DevTools.Contracts`. Set `Private=false` and exclude runtime assets as
   shown in the sample, so the host's contract types are shared.
3. Implement `IDevToolsModule` with a public parameterless constructor, a stable
   unique ID, and `CreatePage(IModuleContext)`.
4. Add an `extension.json` manifest:

```json
{
  "id": "yourname.feature",
  "name": "My feature",
  "entryAssembly": "MyFeature.dll",
  "apiVersion": 1
}
```

5. Build, then put the manifest, extension DLL, `.deps.json` and private dependencies
   into `Extensions/MyFeature/` beside DevTools.exe. Do not copy Contracts.dll.
6. Restart DevTools, enable the extension in Settings, save, and restart again.
   New extensions never execute automatically before opt-in.

## Page API

```csharp
public sealed class MyModule : IDevToolsModule
{
    public string Id => "yourname.feature";

    public ModulePage CreatePage(IModuleContext context)
    {
        var page = new ModulePage(Id, "My feature", "What it does.", "Extensions", "\uE946");
        page.Add("name", "Name", context.Get(Id + ".name"));
        page.Action("Save", token =>
        {
            context.Save(Id + ".name", page["name"].Value);
            return Task.CompletedTask;
        }, context, primary: true);
        return page;
    }
}
```

Use `FieldKind.Choice`, `Toggle`, `Secret`, or `ReadOnly` as needed. Populate
`ModuleField.Choices` for choices. Observable properties update WPF automatically.
Secret fields must not be persisted or logged. Always validate user input before
Windows changes. Use `ConfirmAsync` for consequential operations and honor the
provided cancellation token. CPU-heavy work belongs in `Task.Run`; perform UI model
updates after awaiting it on the UI thread.

The context provides dialogs, folder/file pickers, settings, reports, logging and
process execution. Do not acquire an operation lease inside a registered action:
the command already owns one. Do not use `async void` except the host's ICommand
entry point. API v1 supports declarative forms/actions, not custom WPF views.

## Lifecycle and failures

Modules are loaded once per app start. Duplicate IDs, unsupported API versions,
invalid manifests and construction failures are logged without taking down built-ins.
Disable in Settings and restart before replacing or removing extension files.
No network marketplace, automatic updating or hot reload is included.

Extensions are full-trust .NET code and run as administrator with the host.
Dependency isolation does not restrict file, registry, process or network access.
