# DevTools Studio

A native C# / WPF app for managing Windows and development tools. The Developer
Studio interface starts in dark mode, requests administrator access at startup,
and supports independently installed modules.

## Run

The small Windows x64 ZIP requires **.NET 10 Desktop Runtime (x64)**. No app installer is needed. Extract the complete ZIP to a writable folder and run **DevTools.exe**. Approve the administrator prompt. Keep all application files, `Catalogs/`, and optional `Extensions/` together.

Preferences live in `Data/settings.json` beside the app. Activity logs live in `Data/Logs/`. Windows backups, machine-specific settings and recovery files live in `Data/Machines/<profile>/`, separated by PC and user. Move the whole folder, including `Data`, to carry it with you. Internal Scoop/tool paths follow the app folder; external paths stay explicit. On first use, existing local DevTools data is copied into the current PC profile without deleting its originals. See [portable use](docs/PORTABLE.md).

Settings → Import previous DevTools settings imports selected paths from `devtools.ini`.
It does not apply Windows changes. Restart after importing to refresh all pages.

## Features

- Explorer context menus with 67 supplied application profiles, including Rider,
  executable discovery, custom apps, scope selection, and classic-menu preference.
- URL protocol registration with the 30 supplied profiles, custom handlers,
  previous-command restoration, and Windows default-app Settings integration.
- Shared split-inspector lists for both integration pages: existing registrations
  appear automatically; Scan installed apps shows supported installed options as
  Available. Select a row to configure it. Scanning never adds integrations.
- Live search above each integration list: app names in Context menu, app names
  and URL protocols in Link handlers. Clear with × or Escape.
- Search and Scan installed apps / Refresh added / Add custom share one aligned
  toolbar on both integration pages, with automatic-detection status above it.
- Integrated Windows pages for Explorer & taskbar, Appearance & sign-in, Privacy & suggestions, Power & gaming, and Developer settings: 17 native WinUtil-based controls with backups and read-back verification.
- Compact Start & search controls for disabling Bing web results and the entire Recommended section, with automatic File Explorer restart after recommendation changes. See [Windows preferences](docs/WINDOWS-PREFERENCES.md).
- Windows edition/license detection, custom keys and authorized organization activation.
- Windows Update controls and a Folder inspector for six Windows user folders and custom folders, with reviewed location changes, optional verified file moves and change journals.
- Workspace overview, activity log, dark/light/system themes and extension preferences.
- Startup apps under Windows Tools, with Run/Startup-folder inspection, confirmed additions and removals, and recoverable registry/file archives.

Scoop manager is available under Apps Manager with a compact native table. See docs/SCOOP.md for features and recovery notes. The retired build-engine setup and portable bundle
features are absent. Rider is present only as an Explorer context-menu app profile.

## Build and validate

The original DevTools Studio logo is included as editable SVG, 512px PNG and a
nine-size Windows ICO under src/DevTools.App/Assets. It appears in the title bar,
window/taskbar and executable. To regenerate the PNG/ICO on Windows, run
`./tools/New-AppLogo.ps1`; normal builds use the included assets.

Install the .NET 10 SDK, then run from PowerShell:

```powershell
./Build.ps1 -Test -Package
```

Or open `DevTools.sln` in an IDE supporting the installed .NET SDK. Normal builds use the SDK reference packs without third-party packages. `NuGet.Config` keeps ordinary builds offline. The small package uses the installed .NET 10 Desktop Runtime. Add `-IncludeRuntime` only when a larger self-contained ZIP is needed; that option downloads Microsoft runtime packs from NuGet, or uses `-RuntimePackageSource <local-feed>`. The default package targets `win-x64`; `-Runtime win-arm64` selects an ARM64 build.

The script builds all projects, runs the package-free validation executable and
publishes a portable Windows folder/ZIP under `artifacts/`. It does not execute
system-changing feature actions during testing.

## Compact Windows controls

Update control now applies the **extended pause registry workaround** so the
selected date can appear in **Windows Settings → Windows Update**. Choose **1 year**,
**2 years**, or **Custom date**, then confirm **Apply pause**. Custom dates use local
midnight and must be within ten years. Windows pause requires a concrete end date;
there is no misleading indefinite-pause option.

The app raises FlightSettingsMaxPauseDays and writes matching feature, quality and
overall pause start/end values, plus their pause status. It verifies the values
after writing and reports the earliest of all three expiry dates. It does not
stop services or change NoAutoUpdate. Close and reopen Windows Settings to refresh
the date shown there. This is a build-dependent registry workaround, not a public
supported Windows pause API; matching registry values do not prove Windows will
honor a multi-year pause on every build. Already queued updates may finish, and
security fixes are paused along with other quality updates.

The original types, values and missing entries are backed up. **Restore previous
settings** restores that backup; a previous pause may remain in effect. Timed
restoration runs while signed in or at the next sign-in. Keep the portable app folder and its `Data` folder accessible for recovery. Keep .NET 10 Desktop Runtime installed for the small package's recovery helper. After moving the folder or changing its drive letter, launch DevTools once to rebind a pending recovery task. Changes made in
Windows Settings or by another manager are preserved rather than blindly overwritten.

Older policy-only and service-based DevTools plans remain recoverable. If one is
detected, explicitly use **Restore previous settings** before applying the new
pause method. Applying a pause never silently removes another update policy.
Detected management restrictions and unsupported server installations block Apply;
Windows 10/11 client editions, including Home, can use the workaround.

Implementation references: [pause-limit tweak](https://winburner.com/tweak/pause-updates-longer/)
and [pause date/status implementation](https://github.com/eplord/powershell-letsdoautomation/blob/main/Pause%20Windows%2011%20updates/README.md).

Windows activation detects the Windows edition and reads its license state. It
selects Microsoft's matching public volume key without installing it. Installation
and activation require explicit confirmation. Public keys require licensed
organization activation infrastructure and do not provide a Windows license.
Unsupported editions have no guessed fallback key; an own licensed key can be used.

## Extend and contribute

Start with [architecture](docs/ARCHITECTURE.md), [extension development](docs/EXTENSIONS.md),
and [contribution guidance](CONTRIBUTING.md). The built sample extension is included
in the Windows ZIP but disabled by default. Enable it in Settings and restart.
Enabled extensions share this app's administrator privileges; enable only trusted code.

See [validation and migration limits](docs/VALIDATION.md) before relying on system actions.

## Integration lists

Open Context menu or Link handlers to see registrations already present on the PC.
Use **Scan installed apps** to discover other supported applications in registered
App Paths, PATH, common install directories and the selected Scoop installation.
Unsupported apps can be added through **Add custom**. Creating a profile does not
register it automatically; choose **Add to menu** or **Register handler** afterward.

The right inspector shows executable path, locations or protocol, and the actions
appropriate to the selected row. **Refresh added** reloads registrations. Missing
executables remain visible for repair. Existing entries outside the catalog and
machine-managed Explorer entries are shown read-only. Protected URL defaults open
Windows Settings. Restoring a prior handler requires an unchanged command owned
by this DevTools installation.

Search filters both Added and Available rows as you type, ignores letter case and
extra spaces, and stays active after Scan or Refresh. Multiple search words can
match an app name and its protocol together. A no-match message replaces the list
when nothing matches; clearing search restores all discovered rows. Filtering does
not change registrations or discard inspector edits while the selected row remains
in the results.

## Sidebar navigation

The app opens on Workspace. Apps Manager contains Scoop manager. Windows Preferences groups Start & search with the five preference pages. Developer Tools contains the binary inventory. Windows Tools contains User folders, Update control, Windows activation, Context menu, Link handlers and Startup apps. Startup apps lists the 32-bit and 64-bit Run registry entries and the current-user and common Startup folders, with StartupApproved status where it can be read. It does not include scheduled tasks or services. Adding an app writes a unique value to the current user's Run key after confirmation; it validates and reads back the command without running the selected app. Remove from startup asks before removing the selected Run value or archiving the selected Startup-folder file under `Data/Machines/<profile>/StartupApps/Removed`. Registry values are backed up with their original type and data; files are moved intact, and shortcuts' target apps stay in place. Direct files are archived, not deleted. All-users entries are labeled before removal. Group expansion is remembered; navigating to a page expands its parent group.

Folder inspector usage, file-move behavior and validation limits are documented in [User folders](docs/USER-FOLDERS.md).

In Startup apps, choose **Choose .exe**, review the startup name and optional arguments, then **Add to my startup**. The account is the one running DevTools. New entries take effect at sign-in; Windows startup settings can manage them afterward. Commands are limited to 260 characters, including the executable path and arguments, following Microsoft's [Run registry documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys). An Unknown status means DevTools could not determine whether Windows has enabled the entry. Refresh warnings identify sources that could not be inspected.

