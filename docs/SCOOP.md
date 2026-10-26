# Scoop manager

Open **Apps Manager → Scoop manager**. The compact table uses the shared Studio theme, including Dark, Light and System appearance. Package changes are executed through your installed Scoop PowerShell entry point so Scoop continues to manage dependencies, downloads, hashes, shims and shortcuts.

## Features

- Root detection from saved settings, SCOOP environment variables, Scoop locations on PATH and the default user installation; manual folder selection is available.
- Installed package table: name, version, bucket, hold and update status; multi-selection, column sorting, filtering, executable icons, Refresh, Update selected/all, Uninstall, Hold, Unhold, Reset selected/all and Cleanup old versions.
- Double-click or context menu opens full package details: description, homepage, license, path, size of all physical versions, shim/shortcut definitions and installed versions. Version switching uses `scoop reset app@version`. Folder opening and copying names are available from the context menu.
- Search added buckets, including legacy bucket layouts, with case-insensitive package-name matching and a 500-result limit. Select multiple results for install preview, including versions, descriptions, homepage, license, dependencies and architecture-specific downloads. Info runs Scoop's own information command.
- Buckets and cache: refresh, manifest counts, add known bucket or HTTPS repository, remove selected buckets, update buckets, show cache, clear cache.
- Custom App: name, version, app folder, main executable, shortcut name, optional shim name, description and Copy/Move. Copy is the default. The generated manifest and install metadata live in `apps/name/version`; `current` is a junction; Scoop reset creates shims and Start Menu shortcuts.
- The grouped toolbar separates **Selected apps** (Update selected, Reset selected, Updates → Hold/Allow, Uninstall selected) from **App tools** (Package details, Add / edit PATH, More → folder, homepage, installed version). **Reset selected** recreates shims, shortcuts and PATH for the selected apps. **Maintenance** contains Scoop/bucket updates, old-version cleanup, cache tools, Reset all and PATH rule tools. The standalone Status button has been removed; the Status column remains.
- The Installed table fills the available workspace height and expands when the window is enlarged; its toolbar stays above the list. Package details and PATH rules open from dedicated buttons above the table. PATH folders inside an app are saved relative to its current version; external folders are added to the user PATH. Rules are saved in `<app>/Data/Machines/<profile>/scoop-paths.json`. Import `[Paths]` rules from an older ScoopManager.ini with **Import legacy PATH rules**.
- Saved PATH rules reapply after updates and selected app operations; **Reapply saved rules** also applies imported rules and removes manager-owned absolute entries for uninstalled apps. Read-only Refresh never writes environment settings.
- Commands run in a serial queue, capturing inputs at click time. Output streams to Activity. **Cancel operation** stops the current process tree and clears pending jobs; completed Scoop actions are not automatically undone. Failures stop the remaining queue and show a report.
- Keyboard: F5 refreshes, Ctrl+F focuses the installed filter, Escape clears that filter, Enter submits bucket search. Existing column sorting and multiple selection use standard WPF behavior.

## Confirmations and recovery

Uninstall, reset all, old-version cleanup, bucket changes, clearing cache, installation, switching versions, importing/applying PATH rules and custom imports display confirmations. Scoop's execution policy bypass is scoped to its child PowerShell process. The manager uses the selected Scoop root, never a command found at an unrelated PATH location.

Custom imports reject path traversal, reserved filenames, existing app names, links inside source folders and source folders overlapping the Scoop root. Existing app versions are switched through Installed rather than overwritten by Custom App. Cancellation or failure during staging restores a same-volume moved source and removes only newly created destination data. Once registration succeeds, a failed reset retains the registered app so **Reset** can repair it. Cross-volume Move copies and registers first, then removes the original; if original removal fails, inspect both locations before retrying. Keep backups of valuable portable apps.

PATH manifest edits retain a `.devtools-backup` copy. Absolute user PATH entries are tracked separately in `scoop-owned-paths.json`; pre-existing entries are not claimed and removed later. The prior user PATH is also saved as the `scoop.pathBackup` setting before a change. Scoop itself remains responsible for manifest-relative entries.

## Validation

Run `./Build.ps1 -Test -Package`. Automated tests cover discovery, JSON parsing, version/held status, search, literal command arguments, process failure/cancellation, default Copy, Copy and Move registration, declined/invalid imports, retained registrations after reset failure, queue target capture, install-preview cancellation, relative PATH rules and dark/light/minimum-window rendering. Temporary Scoop roots use a stub `scoop.ps1`; these checks do not install or uninstall real packages or change the user's real PATH.

Manual checks still required on a disposable Windows installation with Scoop: install/update/uninstall a real package; validate shims and Start Menu shortcuts with reset; switch two retained versions; add/remove a bucket; clear cache; test a cross-volume Move and cancellation; verify absolute PATH ownership/removal and imported rules. No live system-changing package operation was performed during implementation.

## Metadata and popup fixes

The manager reads scoop-manifest.json and scoop-install.json first, with fallback to legacy manifest.json and install.json. Hold/Unhold verify the persisted state after each command; the Scoop engine itself uses its time-limited HOLD_UPDATE_UNTIL configuration. Icons are extracted from manifest executable paths or nested executables, with .ico and file-type fallbacks. Failed icon lookups are retried on refresh.

Reports, confirmations, errors, package/PATH editors and file/folder pickers share the Studio window theme and a matching native title bar. Report text starts at the top. Popup rendering is validated in Dark and Light modes.

## Scoop setup

Opening Scoop manager checks the detected or selected installation. A compact status area shows Ready, PATH setup needed, Not installed, or Installation needs attention. **Check again** repeats detection. **Locate Scoop** selects an existing root. Read-only detection never modifies PATH or installs software.

**Add to PATH** confirms adding the selected root's shims folder to the current user's PATH. Existing entries are retained, matching is insensitive to case, quotes and trailing separators, and repeated clicks do not add duplicates. The prior user PATH is saved as `scoop.setup.pathBackup`. The current DevTools process environment is refreshed and the change is broadcast; open new terminals to pick it up. The manager verifies the saved entry and invokes Scoop's version command before reporting success.

When Scoop is missing, **Install Scoop** offers a custom local folder and a confirmation. The destination must be empty and cannot be a drive root, network share or linked directory. The application downloads the [official Scoop installer](https://github.com/ScoopInstaller/Install) from `https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1`, saves it temporarily under the app's Downloads directory, and invokes it with `-ScoopDir` and `-RunAsAdmin` because DevTools runs elevated. Execution-policy bypass is limited to the child process. Output streams to Activity, and **Cancel operation** stops the child process tree. Temporary installer files are removed; partial installation data is kept for inspection. Successful completion verifies the Scoop entry point, launcher, user PATH and version command, then loads the package list.

Automated setup tests use an isolated filesystem and an injected in-memory environment/downloader/process runner. They cover state detection, PATH preservation/idempotence, destination validation, quoting, successful installation verification, cancellation, failures and GUI states at normal/minimum window sizes. The real installer was not executed and the user's real PATH was not changed during implementation.
## Portable repositories from another Windows account

Git may reject a bucket with `detected dubious ownership` if a portable Scoop installation was copied from another Windows account or installation. Scoop can then fail to obtain a commit hash before a package download starts. Before install/update/bucket operations, DevTools checks only the selected Scoop engine and bucket repositories. If Git reports an ownership mismatch, the app lists those exact directories and asks whether to trust them for the command. Declining stops the operation before Scoop runs.

Approval supplies exact `safe.directory` entries through the child process's `GIT_CONFIG_COUNT` environment, preserving inherited command configuration. The settings expire when that process exits. No global Git configuration is edited and no wildcard trust is added. See [Git configuration documentation](https://git-scm.com/docs/git-config) for `safe.directory` and command-scope environment configuration.

Scoop's PowerShell streams are merged into plain text before console output, preventing raw `#< CLIXML` and `<Objs>` message dumps while preserving warnings, errors, command exit codes and cancellation.
