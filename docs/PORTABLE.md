# Portable DevTools Studio

## Run and move

1. Install **.NET 10 Desktop Runtime (x64)** on each PC that will run the small package.
2. Extract the entire Windows x64 ZIP into a writable folder on a local drive or USB drive.
3. Run **DevTools.exe** and approve Windows' administrator prompt.
4. Move or back up the complete folder, including **Data**. Moving only the executable is not sufficient.

The small ZIP uses the installed Microsoft .NET 10 Desktop Runtime, including WPF. The base .NET runtime or ASP.NET runtime alone is not sufficient. Windows 10/11 x64 is still required. Administrator access is needed for the existing Windows management features. Online downloads need internet access; Scoop operations continue to use Scoop and its own tool prerequisites.

Folder publishing is deliberate: the app runs from its extracted files, and the default ZIP omits the runtime to reduce size. The package does not include personal settings or test data.

## Data layout

```text
DevTools.exe
<application files>
Catalogs/
Extensions/
Data/
  settings.json                 Shared appearance, navigation and portable tool paths
  Logs/activity.log             App activity beside the executable
  Machines/<profile>/           Separate PC + Windows-user state
    settings.json               Registry ownership and other machine-specific settings
    StartSearch/backup.json
    WindowsPreferences/
    FolderChanges/
    update-*-plan.json           When update recovery is configured
    UpdateRecovery/              Created only for timed recovery
Scoop/                          Suggested new Scoop location; selected in Scoop manager
```

The profile identifier is a hash of the Windows machine identifier and user SID. On another PC or under another Windows account, DevTools uses a different profile. Backups from one PC are not automatically applied to another PC. The Settings page shows the portable Data folder and Logs folder directly; each has an Open folder action. Older profile logs are copied into Data/Logs as previous-<profile>.log without deleting their originals.

Appearance, sidebar expansion, extension choices, the Scoop root and selected integration executable paths travel with the app. Paths inside the app folder are stored relative to that folder and resolved again after it moves. External installations retain their absolute paths; DevTools does not move them automatically. Machine ownership commands, original PATH backups, folder journals and Windows setting backups retain their exact original contents for restoration.

If the folder is not writable, startup explains the problem. The app does not silently fall back to AppData.

## Existing data

On first launch of a new portable copy, existing `%LOCALAPPDATA%\DevToolsStudio` data is copied into the current PC/user profile. Shared preferences are separated from Windows ownership records. Existing files are not deleted, and the old helper binaries are not copied. Subsequent launches do not repeatedly import old data. A partial import can be retried before the profile is marked ready.

An existing portable Data folder takes precedence when carried to another PC. DevTools does not merge that PC's unrelated legacy settings into your portable preferences.

## Windows changes and timed recovery

The app is portable; changes you explicitly apply to Windows remain on that computer. PATH entries, context menus, scheduled recovery tasks, privacy settings and installed Scoop packages are not undone merely by deleting or unplugging the app.

Timed Windows Update recovery creates its helper under the current portable profile. For the small package, .NET 10 Desktop Runtime must remain installed until restoration completes. The scheduled task receives that profile's exact data path. Keep the portable folder available until restoration completes. If the folder moves or its drive letter changes, launch DevTools once from the new location to rebind a saved recovery plan. A disconnected drive cannot run a recovery task; use **Restore previous settings** before disconnecting when timely recovery is needed. Windows' own update expiry behavior remains separate from this helper.

Existing Scoop installations and their shims may contain absolute paths. Carrying DevTools does not make an external Scoop installation relocatable; use the manager's root selection/PATH/reset controls to repair its registration deliberately.

## Build

```powershell
./Build.ps1 -Test -Package
```

The default portable package is `win-x64`. The script publishes framework-dependent, untrimmed folder output from a fresh staging directory and checks that runtime files are absent. Add `-IncludeRuntime` for the optional larger self-contained package; its runtime packs come from Microsoft's packages on NuGet. An offline package feed can be supplied with `-RuntimePackageSource <directory>`; it must contain the SDK-selected Microsoft runtime/host packs. No runtime package source is added permanently to the user's NuGet configuration.

`src/DevTools.App/Properties/PublishProfiles/Portable.pubxml` also records the small framework-dependent publish settings. Normal source development still needs the .NET 10 SDK. For the default small package, update the installed .NET 10 Desktop Runtime separately.

## Validation

The test suite covers app-local storage, PC/user isolation, moving internal paths, preserving external paths and ownership commands, legacy migration, invalid path traversal, blocked Data folders and complete recovery-helper copying. No Windows setting changes are needed for these tests.

A separate native test launcher verifies runtime selection and renders the 14-page Studio shell in an extracted copy of the release ZIP, without applying Windows changes. The small package loads CoreCLR/WPF from the installed .NET location and keeps settings/logs under its local Data folder. Test-launcher files and generated Data are excluded from the release ZIP. The optional self-contained variant can also be checked with installed runtime discovery disabled.
