# SyncIt Architecture

## Overview

SyncIt is a bidirectional Google Drive sync tool for Windows. It runs as a single process — sync engines on background threads, tray icon on the WinForms UI thread — the same architecture used by OneDrive and Google Drive.

```
SyncIt.sln
├── SyncIt.Core/          Shared library — models, services, safety, helpers
├── SyncIt.App/           Unified single-process app (sync engine + tray icon)
└── SyncIt.Tests/         xUnit test suite (103 tests)
```

## Process Architecture

```
┌─────────────────────────────┐
│         SyncIt.exe          │  ← Single process (like OneDrive)
│  (WinForms ApplicationContext)  │
│                             │
│  UI thread: tray icon,     │
│    context menu, dialogs    │
│                             │
│  Background threads:        │
│    SyncEngine per pair,     │
│    status push loop,        │
│    config watcher           │
└─────────────────────────────┘
```

Single-instance enforced via global mutex. No child processes, no IPC — the tray calls engine methods directly and marshals UI updates via `InvokeUI()`.

## Core Library (SyncIt.Core)

### Models

| Class | Purpose |
|-------|---------|
| `SyncConfig` | Root configuration from `syncit.json` — no credentials |
| `SyncPair` | One local↔remote folder mapping with filters and policies |
| `FileRecord` | Baseline record for a synced file (hash, mtime, revision) |
| `ChangeEvent` | Queued file change from FSW or poll |
| `DeletionRecord` | Audit entry for every delete operation |
| `ConflictPolicy` | Conflict resolution settings per sync pair |
| `SyncStatus` | Status DTOs for UI display |
| `FileTransferProgress` | Per-file upload/download progress tracking |
| `GoogleDocExportMap` | Google Docs MIME type → Office format mapping |

### Sync Engine

`SyncEngine` is the core class — one instance per sync pair. It manages:

- **FileSystemWatcher** — Detects local changes instantly
- **Change queue** — Debounced (500ms), thread-safe queue for FSW events
- **Consumer task** — Processes queued changes (upload/update/delete)
- **Poll loop** — Queries Drive Changes API every N seconds for remote changes
- **Concurrency** — `SemaphoreSlim(3)` limits parallel transfers

#### Local → Remote Flow

```
FileSystemWatcher event
  → OnLocalChange() — filters (glob, size, atomic-writer in-progress)
  → ChangeQueue.Enqueue() — 500ms debounce
  → ConsumeChangesAsync() dequeues
  → HandleLocalChangeAsync()
    → FileLockHelper check (requeue if locked)
    → Conflict detection against remote baseline
    → UploadLocalFileAsync() via SemaphoreSlim(3)
      → GoogleDriveService with Polly retry
      → Update both Local + Remote baselines
```

#### Remote → Local Flow

```
PollRemoteLoopAsync() every N seconds
  → GoogleDriveService.ListChangesAsync() (page token delta)
  → HandleRemoteChangeAsync()
    → Skip if HeadRevisionId matches baseline (echo detection)
    → FastChangeDetector + hash for conflict check
    → DownloadRemoteFileAsync() via SemaphoreSlim(3)
      → AtomicFileWriter: .tmp → MD5 verify → atomic rename
      → Update both Local + Remote baselines
```

### Large File Handling

Files over 5MB use streaming downloads to avoid memory pressure:

- `AtomicFileWriter.WriteFromCallbackAsync()` — Creates a temp `FileStream` and passes it to the Drive SDK, which writes directly to disk instead of buffering in a `MemoryStream`
- Google Drive's resumable upload protocol is used automatically for large files
- `TransferTracker` reports per-file progress via `IProgress<long>` callbacks
- Progress updates are pushed to the UI at 1Hz (throttled)

### Services

| Service | Responsibility |
|---------|---------------|
| `GoogleDriveService` | Drive API v3 wrapper with Polly retry, progress callbacks, and account/quota info |
| `FileStateStore` | SQLite baseline state store (raw ADO.NET, WAL mode) |
| `OAuthService` | OAuth2 with DPAPI-encrypted token storage |
| `ChangeQueue` | Thread-safe debounced change event queue |
| `ConflictResolver` | Conflict detection + 4 resolution strategies |
| `DeletionCoordinator` | Single gateway for all file deletions |
| `GlobFilter` | Include/exclude glob pattern matching (DotNet.Glob) |
| `ConfigWatcher` | Hot-reload `syncit.json` on file change |
| `TransferTracker` | Per-file transfer progress tracking |
| `CloudSyncProvider` | Cloud Files API (cldapi.dll) for Explorer Status column icons |

### Safety Subsystem

Three-layer deletion safety:

```
DeletionCoordinator.SubmitAsync()
  → Layer 1: DeletionSafetyValidator.ValidateAsync()
    → Baseline verification — only known files can be deleted
    → Threshold check — freeze if total pending > MaxDeletionsPerCycle
  → Layer 2: DeletionJournal.RecordAsync()
    → Grace period (default 2 cycles ≈ 60s)
  → Layer 3: Execution
    → Remote: GoogleDriveService.TrashFileAsync() (recoverable)
    → Local: LocalTrashManager.MoveToTrash() → .syncit-trash/
```

If the threshold is exceeded, ALL pending deletions are frozen and the tray shows a warning dialog. The user must explicitly approve or cancel.

### Helpers

| Helper | Purpose |
|--------|---------|
| `AtomicFileWriter` | Write → .tmp → verify MD5 → atomic rename; streaming for large files |
| `FastChangeDetector` | mtime + size fast-path before expensive hashing |
| `FileLockHelper` | Detect locked files, exponential backoff retry |
| `HashHelper` | MD5 (Drive compatibility) + SHA-256 |
| `PathHelper` | Path normalization, relative path math |
| `RetryHelper` | Polly retry policies for Drive API calls |
| `CredentialHelper` | Windows Credential Manager + DPAPI for secrets |
| `ExplorerNotifier` | P/Invoke SHChangeNotify to refresh Explorer overlays |

## Data Storage

All data is stored under `%APPDATA%\SyncIt\`:

| File | Format | Purpose |
|------|--------|---------|
| `syncit.json` | JSON | Configuration (no secrets) |
| `tray-settings.json` | JSON | Tray-only preferences (notifications toggle) |
| `token.bin` | DPAPI-encrypted binary | OAuth2 refresh token |
| `state.db` | SQLite (WAL) | File baseline state per sync pair |
| `delete-journal.db` | SQLite (WAL) | Append-only deletion audit log |
| `logs/syncit-*.log` | Text (Serilog) | App logs (30-day retention) |
| `.syncit-trash/` | Directory | Local trash (30-day retention) |

OAuth client credentials (Client ID + Client Secret) are stored in Windows Credential Manager, not in any file.

## Explorer Status Column (Cloud Files API)

SyncIt uses the Windows Cloud Files API (`cldapi.dll`) to show sync status in Explorer's Status column — the same mechanism used by OneDrive. This replaces the legacy `IShellIconOverlayIdentifier` COM approach which is unreliable on Windows 11.

### How It Works

`CloudSyncProvider` (implements `IOverlayStateStore`) registers the sync folder as a **cloud sync root** via `StorageProviderSyncRootManager`, then uses Cloud Filter P/Invoke calls to manage status:

1. **Registration** — `RegisterSyncRootAsync()` registers with WinRT `StorageProviderSyncRootManager`
2. **Connection** — `CfConnectSyncRoot()` tells Windows the provider is running
3. **Placeholder conversion** — `CfConvertToPlaceholder()` converts files/directories to cloud placeholders (content stays fully local with `HydrationPolicy.AlwaysFull`)
4. **In-sync state** — `CfSetInSyncState()` sets per-file status that drives the Status column icon

### Directory State Propagation

Directories use reference-counting to track sync status:
- When a file becomes **not-synced** → all parent directories up to the sync root are immediately marked not-synced
- When a file becomes **synced** → parent directory's not-synced-child count is decremented; when it reaches 0, the directory is marked synced
- On startup, `MarkAllAsSynced()` converts all files and directories to synced placeholders

### Status Icons

| State | Status Column | Meaning |
|-------|--------------|---------|
| In-sync | Green checkmark | File is up to date |
| Not-in-sync | Blue sync arrows | Transfer in progress or pending |

No admin elevation, COM registration, or native C++ DLL is required — pure .NET P/Invoke.

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Google.Apis.Drive.v3 | 1.68.0 | Google Drive API client |
| Google.Apis.Auth | 1.68.0 | OAuth2 authentication |
| Microsoft.Data.Sqlite | 8.0.11 | SQLite database access |
| DotNet.Glob | 3.1.3 | Glob pattern matching |
| Polly | 8.4.2 | Retry policies for API calls |
| System.IO.Pipelines | 8.0.0 | Efficient pipe I/O |
| System.Security.Cryptography.ProtectedData | 8.0.0 | DPAPI token encryption |

## Design Decisions

1. **SQLite over LiteDB/EF Core** — Raw ADO.NET with SQLite gives full control over connection pooling, WAL mode, and cross-process access (needed for overlay DLL).

2. **Single-process architecture** — Sync engines on background threads, tray icon on WinForms UI thread. No IPC, no child processes — simpler than the two-process model and matches OneDrive/Google Drive.

3. **DPAPI for token encryption** — Windows-native, user-scoped encryption. No master password needed.

5. **Atomic file writes** — All downloads go through temp file → verify → rename to prevent partial files on crash.

6. **Echo detection** — After uploading a file, its Drive revision ID is stored in the baseline. When the Changes API reports that same revision, it's skipped to avoid download-echo loops.

7. **Debounced change queue** — FSW fires multiple events for a single file save (create + modify + modify). The 500ms debounce window collapses these into one sync operation.
