# Start & search

Open **Windows → Start & search**. The compact rows use the native C# equivalent of Chris Titus Tech WinUtil's two Start menu toggles. They target Bing search and the entire Recommended section, including recently added apps.

Choose a row, then **Apply changes**. The page reads back saved registry values; actual Start menu behavior can vary with Windows builds and device policies.

## Bing web search

WinUtil's `WPFToggleBingSearch` sets the current user's `Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled` DWORD to 0 to disable Bing, or 1 to enable it. Local search remains available. Sign out and back in if Search does not refresh.

## Recommended section

WinUtil's `WPFToggleStartMenuRecommendations` sets these machine DWORDs to 1 to hide the section, or 0 to show it:

- `SOFTWARE\Microsoft\PolicyManager\current\device\Start\HideRecommendedSection`
- `SOFTWARE\Microsoft\PolicyManager\current\device\Education\IsEducationEnvironment`
- `SOFTWARE\Policies\Microsoft\Windows\Explorer\HideRecommendedSection`

This requires administrator access, applies to all users, and also disables Windows Spotlight on the lock screen. These effects appear beside the control and in the confirmation. Applying or restoring recommendation settings restarts Explorer after confirmation, closing open File Explorer windows. The restart targets the current Windows session. A separate **Restart Explorer** button allows retrying if the refresh fails.

## Backups and earlier versions

Original registry values, types and absence are saved before writes in `<app>\Data\Machines\<profile>\StartSearch\backup.json`. Each write is read back. Failed or cancelled writes are rolled back where they still match DevTools' change. The backup is retained if rollback cannot complete.

**Restore previous settings** restores owned values only; later changes by other software are preserved. Detected device-management restrictions prevent overwriting managed values. Windows Server and unsupported builds show unavailable controls.

When changing from the earlier DevTools content-suppression method, owned old user settings are restored in the same transaction. Old backup IDs retain their original registry addresses. Recent-document and Jump List settings are no longer directly changed by the recommendation toggle.

## Source and license

Adapted from [Chris Titus Tech WinUtil](https://github.com/ChrisTitusTech/winutil/blob/2f0bdc8fca52e1ebe31a8d98216428061d3f8956/config/tweaks.json), commit `2f0bdc8fca52e1ebe31a8d98216428061d3f8956`, under the MIT license. The original two definitions ship in `Catalogs/start-search-winutil.json`; the full notice ships in `docs/third-party/WINUTIL-LICENSE.txt`.

DevTools implements the same registry settings and Explorer refresh in C#. It does not download or execute the complete WinUtil script at runtime.

## Validation

Automated tests use fake Windows settings and temporary backups. They check parity with the pinned WinUtil definitions, exact write scope, administrator requirements, restore and legacy migration, policy restrictions, cancellation, rollback and restart failure/retry. Dark, light, minimum-size and managed views are rendered. Native status inspection is read-only. Validation does not change the real Windows settings or restart Explorer; actual visual removal of the Start section must be checked after applying inside the app.
