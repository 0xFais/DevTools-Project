# Validation and migration limits

## Executed checks

- Logo resources loaded during all native WPF renders. The published executable's
  embedded icon was extracted and visually inspected. ICO includes 16/20/24/32/40/
  48/64/128/256px frames, with a simplified center at the smallest sizes.

- Extended-pause tests cover exact one/two-year and custom deadlines, pause-limit
  sizing, UTC offsets/leap days, both feature and quality expiry, earliest-expiry
  reporting, Windows-normalized timestamp formats, original value/type/absence
  restoration, first-backup retention, schedule-before-write ordering, stale/early
  tokens, due restoration, scheduler failure, partial-write rollback, failed
  extension, external changes, automatic expiry cleanup, corrupt backup/unknown
  location rejection, file locking, confirmation and explicit legacy-policy recovery.
- Pause tests assert no service or NoAutoUpdate changes. All mutations use fake
  registry/service/scheduler backends. No real pause values were written in validation.
- Legacy policy and service regression checks remain for migration recovery.
- Update pages render default, one/two-year pause, unsupported and custom-date
  states at standard and minimum sizes. Actual WPF duration/date bindings are checked.
  Activation renders remain unchanged.

- Windows controls fix: process results retain separate stdout/stderr. Activation
  and update-search JSON read stdout only, so PowerShell CLIXML diagnostics cannot
  corrupt valid JSON. Nonzero exit codes still fail. Regression checks include a
  real child process writing JSON and diagnostic text to separate streams.
- Missing recovery tasks (HRESULT 0x80070002, including .NET FileNotFoundException)
  are treated as absent during lookup and deletion. Access denied and unrelated
  scheduler failures remain errors. Deletion also tolerates a concurrent removal.
- Additional native read-only inspection confirmed service states. Licensing CIM
  access was denied in this execution session; Task Scheduler connection was also
  unavailable. These are not claimed as successful native integration checks.

- Release solution build on Windows with .NET SDK 10.0.402: zero warnings/errors.
- 102 validation checks covering numeric catalog parsing, extraction path containment,
  PATH preservation, Rider key compatibility, literal process arguments, settings
  persistence/INI parsing, missing layouts, target package planning, process cancellation,
  opt-in extension loading/API rejection, all built-in page construction and layout,
  dark/light/minimum-size WPF renders, elevation manifest and secret-field persistence.
- Integration checks use an in-memory registry and executable fixtures: automatic
  Added lists, read-only external entries, broken paths, scan-only discovery,
  selection/scope synchronization, exact per-app removal, unchecked-scope removal,
  matching URL handlers by executable, UserChoice resolution, restoration ownership,
  explicit custom-profile registration and toolbar visibility/size.
- Both split-inspector pages rendered with registered and available test apps at
  standard and minimum window sizes. Preview images use isolated test fixtures.
- Search checks cover case/whitespace handling, app/protocol and multiword matches,
  Added/Available results, no-match/clear behavior, retained queries during scan and
  refresh, preserved inspector edits/custom drafts, and actual WPF textbox binding
  and clear-command behavior on both pages. Filtered screens were rendered and inspected.
- Toolbar layout checks verify all three buttons align with the search field on
  both pages at standard and minimum window widths, without horizontal overlap.
- WPF screens rendered at 1180×820 and 1024×700 and visually inspected.

## Not executed

Windows controls use fake policy backends, services, scheduler, backup stores and activation backend
for mutation tests. Checks cover schedule-before-disable ordering, due-token guards,
pause extension, rollback, manual resume, declined confirmations, explicit health
checks, edition/key matching (including LTSC and unsupported Home), custom-key
redaction and secret clearing. Both pages render enabled/paused/disabled and
unlicensed/activated/unsupported states at both window sizes. WPF password binding
is checked in both directions.

Native policy enforcement/restoration, Task Scheduler registration/recovery, service mutations, live update search
and license installation/activation were not executed on this PC.

No registry, user environment, licensing, folder-location or service changes were
applied during validation. No live multi-GB compiler/SDK download or extraction was
performed. Native compiler/SDK installation, generated launchers, compilation/runtime
diagnostics and Windows integration actions need end-to-end checks on the target PC.
The catalog planner follows the supplied installer's package IDs; Microsoft can
change catalog formats and SDK dependencies.

## Migration differences

- Uses portable JSON settings beside the executable, with PC/user-specific backups, with explicit import of selected legacy
  INI values. Existing custom app lists and protocol ownership backups are not migrated
  automatically. Recreate custom profiles and retain the old files for legacy restoration.
- Context Menu and Link Handlers now use a shared split inspector: Added entries
  load automatically; Scan installed apps adds installed, supported catalog options
  as Available. Scanning changes no registry. Broken registrations stay visible.
  Command-backed registrations outside the catalog are also listed, with read-only
  details. System-managed entries are not edited by the per-user context-menu actions.
- User folder updates require sign-out/restart instead of automatically killing Explorer.
- Download progress is package-level. Cancellation can leave partial extraction/cache files.
- Compiler runtime files are copied where applicable; the aggressive removal of original
  toolchain directories and telemetry executables is not ported.
- Separate extensions use API v1 declarative pages. Custom XAML and live unload are not
  part of this version. A restart is required after enabling/disabling an extension.
- Scoop is now included under Apps Manager; the portable bundle section remains absent. Scoop validation uses isolated fixture roots and stub commands, not the user's installed apps. See SCOOP.md.

The default small Windows x64 ZIP requires the installed .NET 10 Desktop Runtime. See PORTABLE.md for isolated-host verification.

## Extended pause migration and limits

The visible method now writes the Settings pause limit, dates and statuses.
update-pause-plan.json backs up eleven allowlisted values; update-policy-plan.json
and update-plan.json are retained for explicit legacy recovery. Unknown or corrupt
backups are not discarded. Original DWORD/string types and missing entries are
preserved. Restoring can bring back an older pause rather than forcing updates on.

One/two-year time passage, live pause enforcement, and the Windows Settings page
after real registry writes were not tested end to end. Validation confirms model
logic, fake registry readback, failure handling, and native WPF rendering only.
The workaround can be clamped or rewritten by Windows or management tools. Existing
downloads and pending installations are not guaranteed canceled. Current-user
recovery runs at the deadline while signed in or at next sign-in, and requires
the portable folder and its copied helper/backup data accessible. The small package's helper needs the installed .NET 10 Desktop Runtime.
