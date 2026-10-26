# Folder inspector

Open **Windows → User folders**. The list contains Desktop, Documents, Downloads, Pictures, Music and Videos, plus your saved custom folders. Search by name, path, type or status. Select a row to inspect its current location and choose a new path.

## Windows folders

**Review change** confirms the source, destination and file-handling mode. By default only the registered Windows location changes; existing files stay in the original folder. Select **Move existing files too** to copy and verify the contents before updating the Windows location and removing verified original files. **Restore default** uses the same review and file-handling choice. **Open folder** opens the current location.

The backend uses [SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath) and [SHSetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shsetknownfolderpath) with the six canonical [Known Folder IDs](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid). Read-only inspection falls back to User Shell Folders values and per-user defaults if Windows cannot resolve a known folder. Changes still require the Windows API to succeed and a read-back to confirm the result. Some applications cache folder paths; reopen them or sign out after changing a location.

Folders detected beneath configured OneDrive roots remain visible but cannot be redirected from the inspector until their OneDrive folder backup has been stopped. This avoids competing with the active cloud manager.

## Custom folders

**Add custom folder** saves a name and an existing folder path. Saving or removing an entry never moves or deletes its files. Selecting a custom entry offers a destination and **Review change** to move its contents and update its saved path. It does not change a Windows known-folder setting or create a junction. The old, empty source directory is retained.

Custom entries are stored in `<app>\Data\Machines\<profile>\custom-folders.json`. A custom entry cannot bypass the safeguards for a Windows folder or move the parent of a Windows folder.

## File moves and recovery

- Destinations for file moves must be empty. Files are never merged or overwritten.
- The app inventories files, copies them, verifies SHA-256 hashes, checks that the source inventory has not changed, then changes and verifies the registered/saved location.
- Before removing an original, the app locks it, verifies both its hash and the destination hash again, and marks only that verified original for deletion through its open handle. Source directories are retained; there is no recursive deletion of the user's source folder.
- Cancellation before location switching leaves all originals in place. Cancellation or failure after switching may leave duplicate originals alongside the complete destination. The activity log identifies the stopped stage; inspect both paths before retrying.
- **Change journals** opens `<app>\Data\Machines\<profile>\FolderChanges`. Journals record source/destination, mode, stage, copied files and removed originals. Journals are diagnostic records, not an automatic Undo operation. Keep them until you have checked the result.
- Drive roots, the entire user profile, system/application/application-data directories, network paths, linked paths, cloud placeholders and overlapping Windows folders are rejected. Close applications writing to a folder before moving it. Available permissions, locked files and filesystem capabilities can still prevent completion.

## Validation

Automated checks use a fake Windows folder backend and isolated temporary files. They cover all six folder definitions, location-only changes, verified moves, custom moves, destination collisions, overlap restrictions, OneDrive-managed state, Windows redirection failure, cancellation, changing source files, search, custom persistence/removal, declined reviews, and Dark/Light/minimum-window rendering. They do not redirect or move the user's real folders.

Target-machine manual checks remain necessary for actual Windows redirection, application refresh after sign-out, permissions/locked files and cross-volume moves. Test with disposable data first.
