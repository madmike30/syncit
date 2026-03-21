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
  Forms/             — DashboardForm, ActivityHistoryForm, StatusForm, etc.
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
`StartAsync` → safety check → cancel stale frozen deletions → ensure page token → setup FSW → initial sync (if no baseline) → start consumer task + poll loop concurrently.

### File change flow (local → remote)
FSW event → `OnLocalChange` → filter (glob, size, lock check) → `ChangeQueue.Enqueue` → 500ms debounce → `ConsumeChangesAsync` → `HandleLocalChangeAsync` (conflict check → upload) or `HandleLocalDeleteAsync` (→ DeletionCoordinator).

### File change flow (remote → local)
Poll timer → `RunSyncCycleAsync` → `ListChangesAsync` → `GetRelativePathAsync` (parent walk to root folder) → `HandleRemoteChangeAsync` (echo check → rename detection → conflict check → download) or `HandleRemoteDeleteAsync` (conflict check → DeletionCoordinator).

### Conflict resolution
When both local and remote changed since baseline: `ConflictResolver` applies the configured strategy (default: `RemoteWins`). Options: `LocalWins`, `RemoteWins`, `KeepBoth` (renames local with `.conflict` suffix), `NoConflict`.

## Common Pitfalls

- **SyncIt.exe must be killed before rebuilding** — the DLL gets locked. Use `taskkill //IM SyncIt.exe //F`.
- **State reset for clean sync**: delete `state.db`, `delete-journal.db`, `activity.db` in AppData. This forces a fresh initial sync on next launch.
- **Google Apps MIME types** (`application/vnd.google-apps.*`) cannot be downloaded via `DownloadFileAsync` — they must use Export API or be skipped. Binary download throws "Only files with binary content can be downloaded."
- **Path resolution**: `GetRelativePathAsync` walks the parent chain from a file up to `rootFolderId`. It returns `null` for files outside the sync tree or for the root folder itself.
- **FSW needs `NotifyFilters.DirectoryName`** for folder create/delete/rename events — without it, only file events fire.
