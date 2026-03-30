# SyncIt Sync Recovery Notes (March 30, 2026)

## Summary

On March 30, 2026 we investigated and repaired a live sync incident affecting the user's `Trips` content, with the most visible symptom being duplicate `Japan` folders on Google Drive and stale pending work in the app after earlier failed runs.

The recovery ended with:

- one canonical Drive folder at `Trips/Japan`
- the expected `Thailand` files present locally and remotely
- no remaining pending or frozen deletion rows in the local SQLite journals
- code fixes merged to prevent the same classes of failures from recurring

## User-Visible Symptoms

- Files from the Japan upload landed in two different Drive folders both named `Japan` instead of one shared folder.
- The app showed `16 pending` long after the underlying work had stopped being actionable.
- Dashboard status could drift away from the actual engine state.
- Some remote files used names that are valid in Drive but invalid on Windows, which caused local path handling problems.
- Exported Google Docs could be tracked under the wrong relative path because the remote title did not match the final exported local filename.
- After manual Drive cleanup, stale deletion and baseline state in `%APPDATA%\\SyncIt` continued to reflect old broken paths.

## Root Causes

### 1. Duplicate Drive folder creation race

Concurrent uploads could attempt to create the same parent folder structure at the same time. The check for an existing folder and the create-folder call were not serialized per parent/name pair, so two requests could both conclude that `Japan` did not exist yet and both create it.

### 2. Stale queued work after startup failure

If initial sync or reconciliation failed after queueing change work, the file system watcher and change queue could remain dirty. On the next run, the UI could still surface pending work that no longer represented real work.

### 3. Relative-path inconsistency for remote names

Drive allows names that Windows cannot use directly. Sanitizing only at the final local-write step meant baselines, watcher paths, compare logic, and remote path resolution could disagree about the same item.

### 4. Exported Google Docs were tracked under the wrong local path

Google Docs are exported to a concrete local file such as `.docx`. The engine was still using the raw remote relative path in some state transitions, which could leave state records pointing at a non-existent local filename.

### 5. Dashboard status aggregation was too loose

The dashboard could present a stale or misleading combined state when engine state changed during recovery/startup-error scenarios.

### 6. Manual cleanup left orphaned local state

After Drive-side cleanup and removal of placeholder artifacts, existing `state.db` and `delete-journal.db` rows still described historical broken items. Those records were not active user data anymore, but they could continue to confuse the UI and future delete processing.

## Fixes Applied In Code

## 1. Keyed async folder creation lock

Files:

- `SyncIt.Core/Helpers/AsyncKeyedLocker.cs`
- `SyncIt.Core/Services/GoogleDriveService.cs`
- `SyncIt.Tests/Helpers/AsyncKeyedLockerTests.cs`

What changed:

- Added a keyed async lock utility.
- Wrapped Drive folder ensure/create logic with a lock key composed from parent folder ID plus folder name.
- Re-checked for an existing folder inside the lock before creating anything.

Result:

- Concurrent uploads now converge on a single Drive folder for the same logical path.

## 2. Change queue reset on failed startup

Files:

- `SyncIt.Core/Services/IChangeQueue.cs`
- `SyncIt.Core/Services/ChangeQueue.cs`
- `SyncIt.Core/Services/SyncEngine.cs`
- `SyncIt.Tests/Services/SyncEngineTests.cs`

What changed:

- Added `Clear()` to the queue contract and implementation.
- On startup failure during initial sync/reconciliation, the engine now stops the file system watcher and clears queued work before entering the error state.

Result:

- Stale `pending` counts do not survive a failed startup path.

## 3. Remote path sanitization moved earlier

Files:

- `SyncIt.Core/Helpers/PathHelper.cs`
- `SyncIt.Core/Services/GoogleDriveService.cs`
- `SyncIt.Core/Services/SyncEngine.cs`
- `SyncIt.Tests/Helpers/PathHelperTests.cs`

What changed:

- Added helpers for sanitizing remote path segments and composing remote-relative paths consistently.
- Applied sanitization when building remote paths, populating the folder cache, resolving parent chains, and enumerating remote trees.

Result:

- Local baselines, watcher paths, remote compares, and downloads now agree on the same Windows-safe relative path.

## 4. Exported Google Docs now use the real local filename in state

Files:

- `SyncIt.Core/Services/SyncEngine.cs`

What changed:

- The engine now treats the exported path, including the export extension such as `.docx`, as the canonical sync-relative path for Google Docs.

Result:

- Exported files no longer drift between remote title and final local filename in baselines or later comparisons.

## 5. Dashboard status handling tightened

Files:

- `SyncIt.App/SyncItApp.cs`
- `SyncIt.App/Forms/DashboardForm.cs`

What changed:

- Adjusted status aggregation and dashboard update flow so engine state is represented more accurately during startup failures and recovery.

Result:

- The dashboard reflects the actual sync state more reliably.

## Live Cleanup Performed

The live environment also required one-time cleanup outside the repo:

- deduplicated Drive `Trips/Japan` down to one canonical folder
- removed stale Thailand placeholder artifacts from both Drive and the local sync root
- cleaned stale rows from `%APPDATA%\\SyncIt\\delete-journal.db` and `%APPDATA%\\SyncIt\\state.db` after confirming they were orphaned leftovers from earlier broken runs

One-off inspection and cleanup helpers were created under `.codex_tmp/` during the recovery. These were operational tools for this incident and are intentionally not part of the product commit.

## Verification Performed

As of March 30, 2026:

- `dotnet test SyncIt.Tests\\SyncIt.Tests.csproj` passed with `110/110`
- the app was rebuilt, republished, and restarted successfully
- logs showed a clean startup around `2026-03-30 17:07`
- no active pending or frozen deletion rows remained after DB cleanup
- Drive `Trips/Japan` existed as a single folder and contained the expected files
- local and remote `Trips/Thailand` contents matched the recovered expected set, including exported `.docx` files for Google Docs

## Lessons Learned

### 1. Sanitize remote names before they touch state

If sanitization happens only when writing to disk, the system ends up with multiple identities for the same file. The canonical sync-relative path must already be Windows-safe before it reaches baselines, queues, filters, or compare logic.

### 2. Exported cloud-native documents need a canonical final path

Google Docs are not local files until export. The exported filename must be the filename used everywhere in local state and reconciliation, or later runs will treat the same item as missing, new, or both.

### 3. Folder creation must be serialized by logical key

Checking for an existing remote folder is not enough when uploads race. The existence check and create call must be guarded as one critical section keyed by parent plus name.

### 4. Startup failure must leave the engine clean

A failed initial sync cannot be allowed to leave watchers running or queue state behind. Error recovery should actively clear volatile state so the next run starts from reality.

### 5. Manual data repair should be followed by local state repair

When we remove or merge items directly in Drive after a bad sync episode, local journals and baselines may still describe the bad world. Recovery is not complete until those local records are cleaned or re-baselined.

### 6. Recovery work needs one written timeline

The incident crossed code fixes, Drive cleanup, and SQLite cleanup. Keeping all of that in one note is valuable because future debugging depends on understanding the interaction between product behavior and one-time operator actions.

## Recommended Guardrails

- Keep regression tests for queue cleanup, path sanitization, and keyed folder creation in place.
- When remote-relative-path logic changes, verify both normal files and exported Google Docs.
- After any manual Drive cleanup, inspect both `delete-journal.db` and `state.db` before declaring recovery complete.
- Treat dashboard-state mismatches as a product issue, not just a UI issue, because they often point to lifecycle cleanup problems underneath.
