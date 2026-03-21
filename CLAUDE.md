# CLAUDE.md — SyncIt Project Guide

## Project Overview

SyncIt is a Windows Google Drive sync application (like OneDrive/Google Drive desktop). It runs as a single-process WinForms app with a system tray icon, background sync engines, and a dashboard UI.

## Tech Stack

- **.NET 8.0 / C# 12**, target: `net8.0-windows10.0.19041.0`
- **WinForms** (ApplicationContext pattern — no main window, tray icon + popup forms)
- **Google Drive API v3** — Changes API polling, file upload/download, Google Docs export
- **SQLite** (WAL mode) — state store, deletion journal, activity log, overlay state
- **Serilog** — structured logging to `%APPDATA%\SyncIt\logs\`
- **Polly** — retry policies for Drive API calls
- **xUnit + Moq + FluentAssertions** — test project

## Architecture

```
SyncIt.App/          — WinForms entry point, tray icon, dashboard UI, forms
  SyncItApp.cs       — Main ApplicationContext: owns tray icon + sync engines
  Forms/             — DashboardForm, FirstRunForm, AccountForm, DriveFolderPickerForm, etc.
SyncIt.Core/         — All sync logic (no UI dependency)
  Services/          — SyncEngine, GoogleDriveService, ChangeQueue, DeletionCoordinator, etc.
  Safety/            — DeletionSafetyValidator, DeletionJournal (3-layer deletion safety)
  Models/            — SyncConfig, SyncPair, FileRecord, ChangeEvent, etc.
  Helpers/           — AtomicFileWriter, HashHelper, PathHelper, RetryHelper, etc.
  IO/                — ThrottledStream (bandwidth control)
SyncIt.Tests/        — xUnit test project
```

## Key Design Decisions

### Single-process architecture
No separate service/tray processes. SyncIt.App runs sync engines on background threads and the tray icon on the UI thread. Previous SyncIt.Service and SyncIt.Tray projects were removed.

### Deletion safety pipeline (3 layers)
1. **Baseline validation** — only delete files with known sync history
2. **Threshold freeze** — if >50 pending deletions, freeze ALL and require user approval via dialog
3. **Grace period** — deletions wait 2 poll cycles (~60s) before executing; soft-delete to Drive Trash / local `.syncit-trash`

### Echo detection
After uploading, the Changes API echoes the change back. `DriveRevisionId` comparison prevents re-downloading our own uploads.

### Google Docs handling
Google Apps files (Docs, Sheets, etc.) use Export API, not Download. `GoogleDocExportMap` maps MIME types to export formats. Non-exportable types (shortcuts, forms, maps) are skipped.

### Change detection
- **Local**: FileSystemWatcher → ChangeQueue (500ms debounce) → consumer task
- **Remote**: Polling via Drive Changes API with page tokens (default 30s interval)

## Build & Run

```bash
# Build
dotnet build

# Publish (creates publish/ directory)
dotnet publish SyncIt.App/SyncIt.App.csproj -c Release -o publish --self-contained false

# Run (silent, no console window)
wscript SyncIt.vbs

# Run tests
dotnet test SyncIt.Tests/
```

The `SyncIt.vbs` script launches `publish/SyncIt.exe` silently. `SyncIt.bat` also works but shows a brief console flash.

## Configuration

Config lives at `%APPDATA%\SyncIt\syncit.json`. Key settings:
- `syncPairs` — array of `{ id, localPath, remoteFolderId, remoteFolderName }`
- `pollIntervalSeconds` — remote polling interval (default 30)
- `maxDeletionsPerCycle` — freeze threshold (default 50)
- `deletionGraceCycles` — wait cycles before executing delete (default 2)
- `maxUploadKBps` / `maxDownloadKBps` — bandwidth throttle (0 = unlimited)
- OAuth tokens stored in `%APPDATA%\SyncIt\token.bin`
- Client ID/secret stored in Windows Credential Manager (not in config)

## Data Files (all in %APPDATA%\SyncIt/)

| File | Purpose |
|------|---------|
| `state.db` | File baselines (local + remote side records, page tokens) |
| `delete-journal.db` | Deletion records with status tracking |
| `activity.db` | Sync activity history |
| `overlay-state.db` | Windows Explorer overlay icon state |
| `token.bin` | OAuth refresh token |
| `syncit.json` | User configuration |

## Important Patterns

### SyncEngine lifecycle
`StartAsync` → safety check → cancel stale frozen deletions → ensure page token → **validate remote folder access** → setup FSW → initial sync (if no baseline) → start consumer task + poll loop concurrently. Initial sync uses `Parallel.ForEachAsync` with bounded concurrency (4) for uploads, downloads, and hash comparisons.

### File change flow (local → remote)
FSW event → `OnLocalChange` → filter (glob, size, lock check) → `ChangeQueue.Enqueue` → 500ms debounce → `ConsumeChangesAsync` → `HandleLocalChangeAsync` (conflict check → upload) or `HandleLocalDeleteAsync` (→ DeletionCoordinator).

### File change flow (remote → local)
Poll timer → `RunSyncCycleAsync` → `ListChangesAsync` → `GetRelativePathAsync` (parent walk to root folder) → `HandleRemoteChangeAsync` (echo check → rename detection → conflict check → download) or `HandleRemoteDeleteAsync` (conflict check → DeletionCoordinator).

### Conflict resolution
When both local and remote changed since baseline: `ConflictResolver` applies the configured strategy (default: `RemoteWins`). Options: `LocalWins`, `RemoteWins`, `KeepBoth` (renames local with `.conflict` suffix), `NoConflict`.

### Account switching & state DB lifecycle
`SwitchAccountAsync` in SyncItApp: stop engines → delete token → re-authorize (with account picker) → dispose old DriveService → **reset all state DBs** (state.db, delete-journal.db, activity.db) via `SqliteConnection.ClearAllPools()` + file deletion → validate new account's access to configured Drive folder → recreate ActivityLog + CloudSyncProvider → restart engines. State DBs are also reset when the Drive folder changes (config watcher detects `RemoteFolderId` change).

### Path resolution safety
`GetRelativePathAsync` walks parent chain from file to `rootFolderId` using `DriveFolderCache` for fast resolution. `RunSyncCycleAsync` rejects any path starting with `My Drive/` as a safety net against cache pollution from the Changes API (which returns changes across all of Drive, not just the sync folder).

## Live Smoke Tests

When a live test is required (after significant changes to sync logic, FSW handling, deletion pipeline, or activity logging), run through all of these tests. Requires a running SyncIt instance with at least one configured sync pair.

### Prerequisites
```bash
# Build, test, publish
taskkill //IM SyncIt.exe //F 2>/dev/null
dotnet build && dotnet test SyncIt.Tests/ && dotnet publish SyncIt.App/SyncIt.App.csproj -c Release -o publish --self-contained false

# Launch and wait for engine start
wscript SyncIt.vbs
# Verify running: tasklist //FI "IMAGENAME eq SyncIt.exe"
# Check logs: tail %APPDATA%\SyncIt\logs\syncit-<date>.log
```

### Test Matrix

Run each test sequentially, waiting ~35s between tests for a full poll cycle. Verify each operation in the log file.

| # | Operation | Command | Expected Log |
|---|-----------|---------|--------------|
| 1 | **Create file** | `echo "test" > <syncfolder>/test-file.txt` | `Uploaded: test-file.txt` + echo skip on next poll |
| 2 | **Modify file** | `echo "modified" >> <syncfolder>/test-file.txt` | `Updated: test-file.txt` |
| 3 | **Create folder** | `mkdir <syncfolder>/test-folder` | `Created Drive folder 'test-folder'` + `Folder created: test-folder` |
| 4 | **Create nested file** | `echo "nested" > <syncfolder>/test-folder/nested.txt` | `Uploaded: test-folder/nested.txt` |
| 5 | **Rename file** | `mv <syncfolder>/test-file.txt <syncfolder>/renamed.txt` | `Uploaded: renamed.txt` + `Trashed Drive file` (old) |
| 6 | **Rename folder** | `mv <syncfolder>/test-folder <syncfolder>/folder-renamed` | `Folder renamed: 'test-folder' → 'folder-renamed'` |
| 7 | **Delete file** | `rm <syncfolder>/renamed.txt` | `Deletion queued` → (wait 2 cycles) → `Executed deletion` + `Trashed Drive file` |
| 8 | **Delete folder** | `rm -rf <syncfolder>/folder-renamed` | Delete events for folder contents |

### Verification
- **Logs**: `tail -100 %APPDATA%\SyncIt\logs\syncit-<date>.log` — check for errors, successful operations
- **Activity DB**: Query `activity.db` for records matching all ActivityType values: Upload, Download, Renamed, FolderCreated, Deleted
- **Echo detection**: After each upload, the next poll cycle should show `Skipping remote change ... revision unchanged (own upload echo)`
- **Deletion safety**: File deletions must go through validation → journal → grace period (2 cycles) → execute. Files without baselines are rejected.

## Common Pitfalls

- **SyncIt.exe must be killed before rebuilding** — the DLL gets locked. Use `taskkill //IM SyncIt.exe //F`.
- **State reset for clean sync**: delete `state.db`, `delete-journal.db`, `activity.db` in AppData. This forces a fresh initial sync on next launch.
- **Google Apps MIME types** (`application/vnd.google-apps.*`) cannot be downloaded via `DownloadFileAsync` — they must use Export API or be skipped. Binary download throws "Only files with binary content can be downloaded."
- **Path resolution**: `GetRelativePathAsync` walks the parent chain from a file up to `rootFolderId`. It returns `null` for files outside the sync tree or for the root folder itself.
- **FSW needs `NotifyFilters.DirectoryName`** for folder create/delete/rename events — without it, only file events fire.
- **Empty folders must be synced**: `ListFilesAsync` must yield folder entries (not just recurse into them), and `InitialSyncAsync` must enumerate local directories separately from files. `DownloadRemoteFileAsync` handles folders via `Directory.CreateDirectory`.
- **Cloud Files pin state constants**: `CF_PIN_STATE_PINNED = 1`, NOT 2 (which is `UNPINNED`). Getting this wrong causes folders to show blue cloud icons instead of green checkmarks.
- **Cloud Files overlay for folders**: Both `SetInSyncState` AND `CfSetPinState(PINNED)` must be called for items to show green checkmarks. `CfConvertToPlaceholder` with `CF_CONVERT_FLAG_MARK_IN_SYNC` alone is not sufficient — items still need to be pinned.
- **FolderBrowserDialog hangs on UI thread**: The COM-based dialog can deadlock with the WinForms message pump. Run it on a dedicated STA thread with `thread.SetApartmentState(ApartmentState.STA)`.
- **OAuth account picker**: `GoogleWebAuthorizationBroker.AuthorizeAsync` auto-selects the last Google account. To force the account picker, use a custom `GoogleAuthorizationCodeFlow` with `Prompt = "select_account"` on the `Initializer`. Note: `"consent"` forces re-consent but may auto-select the same account — use `"select_account"` to force the account chooser. The dedicated `Prompt` property on `GoogleAuthorizationCodeFlow.Initializer` is preferred over `UserDefinedQueryParams`.
- **State DB reset on account/folder change**: Stale `state.db` records from a previous account or folder cause phantom deletion detections (files exist in baseline but not locally → treated as "deleted locally" → trashed from Drive). Always reset state DBs when switching accounts or changing the sync folder.
- **SQLite connection pooling**: `Microsoft.Data.Sqlite` pools connections internally. Call `SqliteConnection.ClearAllPools()` before deleting `.db` files, otherwise you get "file in use" errors even after disposing all store objects.
- **DriveFolderCache pollution**: The Changes API returns changes across ALL of Google Drive, not just the sync folder. Folder entries from changes outside the sync tree can pollute the cache, causing `GetRelativePathAsync` to resolve paths like `My Drive/folder/file.txt` instead of just `file.txt`. The `My Drive/` path rejection in `RunSyncCycleAsync` is a safety net for this.
- **Config watcher stale captures**: Event handler lambdas in `ConfigReloaded` must use instance fields (`_driveService`, `_engineCts.Token`) rather than captured locals from the startup scope. After account switch, the original captured references are stale/cancelled.
