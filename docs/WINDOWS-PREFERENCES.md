# Integrated Windows preferences

The Windows sidebar contains five native C# preference pages immediately after **Start & search**, followed by the existing Integrations, User folders, Update control and Windows activation entries. Rounded sidebar groups and the Studio theme are retained. The new pages keep their action bar visible while the setting rows scroll, including at the minimum supported window size.

## Included controls

| Page | Controls |
|---|---|
| Explorer & taskbar | File extensions; hidden files; classic right-click menu; left/center alignment; Search button; Task View button |
| Appearance & sign-in | Windows dark mode; always-visible scrollbars; detailed startup/shutdown messages; sign-in blur |
| Privacy & suggestions | Disable activity publishing/uploading; disable location tracking; disable consumer features |
| Power & gaming | Enable hibernation; Game Mode; Ultimate Performance plan |
| Developer settings | Long-path support for compatible applications |

These 17 controls adapt the selected [Chris Titus Tech WinUtil definitions](https://github.com/ChrisTitusTech/winutil/blob/2f0bdc8fca52e1ebe31a8d98216428061d3f8956/config/tweaks.json). The page checks saved settings on first opening. On means the named feature is enabled: for a row named **Disable location tracking**, On means tracking is disabled. Alignment and power-plan controls use choices instead of inverse switches.

Only changed rows are applied. A confirmation lists the chosen settings, their effects and their scope. The app reads values back after writing and reports saved settings, not a guarantee of rendered Windows behavior. Windows updates and device policy may alter behavior later.

## Scope and effects

- Explorer, taskbar, Windows dark mode, scrollbars and Game Mode use current-user settings. Windows dark mode changes Windows and supported apps; DevTools follows its separate theme setting.
- Detailed startup messages, sign-in blur, privacy controls, hibernation, the power plan and long paths require administrator access. The app marks them as applying to all users.
- Classic menus and taskbar alignment require Windows 11. Classic menu behavior depends on the installed Windows build.
- The Microsoft consumer-features policy is available on Enterprise/Education editions, including corresponding IoT editions. On Home/Pro the row stays visible with an explanation instead of claiming an effective change.
- Location changes include the `lfsvc` startup mode, app location consent, sensor permission and automatic map updates. As in WinUtil, this configures startup without forcibly stopping a running location service. Location-dependent apps are affected.
- Activity changes preserve clipboard history while disabling activity publishing/uploading.
- Turning off hibernation also affects Fast Startup and removes the hibernation file. Re-enabling hibernation recreates it. Hardware or policy can prevent a requested change.
- Ultimate Performance is offered on desktops without a detected battery. Hardware may restrict available plans. Enabling it duplicates WinUtil's Ultimate Performance template and activates a DevTools-owned copy. Turning it off restores the previous active plan and removes only the DevTools copy. **Unlike WinUtil's remove action, DevTools does not reset all power plans to defaults or delete unrelated custom plans.** If another app changes the active plan, automatic restore preserves that external change.
- Long paths need support in each application as well as the Windows setting. Restart affected applications or Windows as necessary.
- Known device-management provider flags for the included privacy policies block conflicting changes. This detection is not a complete inventory of all domain/MDM policy mechanisms; policies can be reapplied later.

## Explorer refresh

Changes to file extensions, hidden files, classic menus and alignment restart File Explorer once after a successful batch. Applying or restoring Start menu recommendations also automatically restarts File Explorer. The confirmation explains that open Explorer windows close and the desktop/taskbar briefly reload.

After settings are saved, a late Cancel does not skip the required Explorer restart. If restart fails, settings and backups remain saved, and **Restart Explorer** provides a retry. Only Explorer processes in the current Windows session are targeted. DevTools does not sign out or restart Windows automatically.

## Backup and restore

Each new page has its own backup at `<app>\Data\Machines\<profile>\WindowsPreferences\<page-id>.json`. Original registry values, types and absence, service startup mode, menu-key presence and active power-plan state are captured before the first write. Start & search uses the same current-PC profile, under `StartSearch/`. Existing local backups are copied during first-use migration.

**Restore previous settings** restores values still matching DevTools' last change. External edits and newly managed values are preserved. Each page can restore only its own fixed resource allowlist. Failed or cancelled batches roll back completed writes where possible; the backup is retained if recovery cannot complete. A lock prevents simultaneous operations on the same page. Backups cover the configured resources, not complete system state or a Windows restore point.

The classic-menu adapter records key presence as well as its default value, so an empty handler key is removed when switching the override off and original empty keys can be restored. Additional unrelated key content is preserved rather than recursively deleted.

## Source, license and implementation

Source commit: `2f0bdc8fca52e1ebe31a8d98216428061d3f8956`. The selected definitions and Ultimate Performance source are included in `Catalogs/windows-preferences-winutil.json`. The MIT notice ships in `docs/third-party/WINUTIL-LICENSE.txt`.

DevTools uses C# registry access, Service Control Manager and Windows power APIs. The built-in `powercfg.exe` handles hibernation and power-scheme duplication. No complete remote WinUtil script is downloaded or executed at runtime. Maintenance, repair, feature-installation and broad service-optimization presets are not added by these pages.

## Validation

Tests use fake Windows resources and temporary backups for all writes and Explorer restarts. They cover source parity, every feature's apply/restore round trip, old types and missing values, service rollback, policy removal, cross-page rejection, cancellation, external edits, administrator/build/edition restrictions, desktop requirements, custom power-plan preservation, classic-menu key presence and Explorer failure/retry. Dark/light and minimum-size WPF renders verify the integrated pages and visible Apply controls. Native inspection is tested read-only; real Windows changes and actual Start/Explorer behavior are not exercised automatically.
