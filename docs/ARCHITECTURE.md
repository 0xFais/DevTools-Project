# Architecture

## Windows workspaces

The optional UI-neutral `WindowsWorkspace` supplies status and action bindings to
the shared compact WPF view. Activation uses an injectable backend and a separate
edition-to-public-key catalog. Detection and license reads never install keys.
Custom keys are transient secret fields and are cleared after an activation attempt.

UpdatePauseController coordinates injectable pause-backend, backup-store and
scheduler interfaces. The native backend writes only eleven allowlisted values
under WindowsUpdate/UX/Settings and WindowsUpdate/UpdatePolicy/Settings. It raises
FlightSettingsMaxPauseDays, sets UTC start/end values and feature/quality pause
status, and verifies readback. Display uses the earliest actual end date. Windows
date-format normalization is compared by UTC instant, while backups retain their
original string representation and registry type.

An atomic update-pause-plan.json journal preserves the first original snapshot,
the applied snapshot, a recovery token and deadline. Shared file locking serializes
pause and legacy-policy recovery. Recovery is scheduled before mutation; failures
roll back and retain backups if rollback cannot finish. Restore checks ownership
and retains external changes. Expired Windows cleanup of dates/status is respected.
Unknown registry names/types and corrupt backups block writes.

Recovery copies fixed app components into `<app>/Data/Machines/<profile>/UpdateRecovery`
and registers a highest-privilege current-interactive-user task. It runs at the
deadline or next sign-in, without SYSTEM or stored passwords. Failed invocations
retry three times one minute apart. --resume-updates checks due pause, legacy
policy and service tokens before opening a window. The recovery helper receives the exact machine-data path. The small package requires the installed .NET 10 Desktop Runtime; optional self-contained packages also copy their runtime files.

UpdatePolicyController and UpdateController remain for older journals and explicit
recovery only. New pause application never changes NoAutoUpdate or stops services.
The Settings registry workaround is build-dependent and is not a documented public
Windows pause API. Readback validation is not an end-to-end enforcement guarantee.

```text
DevTools.App (WPF shell, MVVM rendering, themes, dialogs)
    ├── DevTools.Modules (one feature folder / module)
    ├── DevTools.Infrastructure (processes, settings, discovery, extension loading)
    └── DevTools.Contracts (shared module interface and presentation models)

Extensions/<name>/extension.json + module DLL + private dependencies
    └── DevTools.Contracts (shared from the host)
```

## Boundaries

- **Contracts:** `IDevToolsModule` supplies a `ModulePage`. Fields declare values,
  choices, toggles and secrets; actions expose async commands. This assembly has
  no WPF reference, allowing extension logic to be tested without a window.
- **App:** composition root registers built-ins and enabled extensions, groups
  navigation, renders declarative pages and provides native dialogs. XAML templates
  and brushes live here. Code-behind is limited to window controls, secure-field
  synchronization and log scrolling.
- **Infrastructure:** atomic JSON settings, legacy INI parsing, executable discovery,
  literal process arguments, process-tree cancellation and extension dependency contexts.
- **Modules:** feature-specific state, confirmation and orchestration. Windows registry
  operations remain in their feature modules; move shared policies to Infrastructure
  when a second module needs them. Portable compiler layout, diagnostics and installer
  processing are separate from its page definition.
- **Tests:** package-free executable covering deterministic logic, real process
  cancellation, extension loading, page construction and off-screen WPF rendering.

## Shared split inspector

`IntegrationBrowser` and `IntegrationItem` are UI-neutral optional page contracts.
`ModulePage.Browser` opts a module into the shared `IntegrationBrowserView`, leaving
other modules and extension pages on the existing declarative form renderer.
Toolbar, inspector and advanced action collections reference the same ModuleAction
objects registered on the page, preserving global operation/cancellation behavior.
Rows include current executable path, status, locations/protocol and editability.
Selection drives inspector fields; refreshing retains the selected row where possible.

The browser keeps its full discovered snapshot separate from the visible Items
collection. SearchText filters it in memory; Link handlers opts into protocol matches
with SearchIncludesLocations. Filtering preserves matching row objects and selection
so inspector edits survive typing. Scan/refresh calls ReplaceItems without resetting
the query. Search is presentation state and never invokes registration services.

`ContextMenuRegistrations` and `LinkRegistrations` separate registry interpretation
and changes from page orchestration. They depend on `IIntegrationRegistry`; discovery
depends on `IInstalledAppDiscovery`. Tests inject an in-memory registry and executable
fixtures, so registration/update/removal/restoration checks never change Windows.
The native discovery implementation walks each configured root once per scan,
checks cancellation between directories, and does not follow directory junctions.

## Module lifecycle

The shell calls `CreatePage` once per module, on the UI thread. Construction may
read local settings and inspect existing state; it must not apply system changes.
Actions run asynchronously. A global operation lease prevents conflicting actions
across pages. Input fields are disabled during operations; navigation and cancellation
remain available. Exceptions are logged and shown in a scrollable report.

## Downloads and processes

The compiler installer reads Microsoft's Stable/Insiders channel and package catalogs,
resolves compiler packages and SDK MSI/cabinet payloads, verifies each package hash,
extracts VSIX `Contents/` entries with destination containment checks, and uses Windows
Installer administrative extraction for SDK packages. Cache files are scoped to
`<toolchain>/.devtools-cache`. Cleanup removes only files tracked by the operation.
No Visual Studio registration or bundle copying is performed.

System processes use `ProcessStartInfo.ArgumentList` rather than shell concatenation.
Generated CMD launchers validate paths and quote arguments; PowerShell setup scripts
use literal path segments. Launchers are generated per target architecture. Cancellation
can leave partially extracted files, which a later installation can reuse/overwrite.

## Extensions

One `AssemblyLoadContext` per extension resolves private dependencies with
`AssemblyDependencyResolver`, while sharing the host's Contracts assembly.
This follows [Microsoft's plugin architecture](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support).
Extension API v1 renders declarative fields/actions through the same WPF templates
as built-ins; it does not load arbitrary extension XAML or hot-unload assemblies.
Loading isolation manages dependencies; it is not a permissions boundary.

## UI conventions

Developer Studio uses a compact navigation rail, lavender accent, dark neutral
surfaces, rounded panels and a fixed activity area. Shared theme brushes support
Dark, Light and System. Main feature pages scroll independently of navigation/logs.
Use `Themes/Studio.xaml` to change the design once for all modules.
