# SyncIt QA Audit Report

## Project Overview
SyncIt is a Windows Service that provides bidirectional Google Drive synchronization for a single user's laptop. It consists of three projects:

- **SyncIt.Core** — Shared library (models, services, safety subsystem, IPC, helpers)
- **SyncIt.Service** — Windows Service host (BackgroundService)
- **SyncIt.Tray** — WinForms system tray companion app

## File Inventory

### Models (`SyncIt.Core/Models/`)
| File | Purpose |
|---|---|
| `SyncConfig.cs` | Root config from `syncit.json` (no credentials) |
| `SyncPair.cs` | Single sync pair definition (local path + Drive folder) |
| `FileRecord.cs` | Baseline record for a synced file |
| `ChangeEvent.cs` | FSW/poll change event queued for processing |
| `DeletionRecord.cs` | Audit entry for every delete (journal) |
| `ConflictPolicy.cs` | Conflict resolution settings |
| `SyncStatus.cs` | Status DTOs for IPC push |
| `GoogleDocExportMap.cs` | Google Docs MIME type → export format map |

### Services (`SyncIt.Core/Services/`)
| File | Purpose |
|---|---|
| `SyncEngine.cs` | Core bidirectional sync engine per SyncPair |
| `GoogleDriveService.cs` | Drive API v3 wrapper with Polly retry |
| `FileStateStore.cs` | SQLite baseline state store (raw ADO.NET) |
| `OAuthService.cs` | OAuth2 with DPAPI-encrypted token storage |
| `ChangeQueue.cs` | Thread-safe debounced change event queue |
| `ConflictResolver.cs` | Conflict detection + resolution strategies |
| `DeletionCoordinator.cs` | Single gateway for all file deletions |
| `GlobFilter.cs` | Include/exclude glob pattern matching |
| `ConfigWatcher.cs` | Hot-reload syncit.json on file change |

### Safety Subsystem (`SyncIt.Core/Safety/`)
| File | Purpose |
|---|---|
| `DeletionSafetyValidator.cs` | Threshold check + baseline verification |
| `DeletionJournal.cs` | Append-only SQLite audit log of all deletions |
| `LocalTrashManager.cs` | Moves files to `.syncit-trash/` instead of deleting |

### Helpers (`SyncIt.Core/Helpers/`)
| File | Purpose |
|---|---|
| `AtomicFileWriter.cs` | Write → .tmp → verify MD5 → atomic rename |
| `FastChangeDetector.cs` | mtime+size fast-path before hashing |
| `FileLockHelper.cs` | Detect locked files, exponential backoff |
| `HashHelper.cs` | MD5 (Drive compat) + SHA-256 |
| `PathHelper.cs` | Path normalization, relative path math |
| `RetryHelper.cs` | Polly retry policies for Drive API |
| `CredentialHelper.cs` | Windows Credential Manager + DPAPI |

### IPC (`SyncIt.Core/Ipc/`)
| File | Purpose |
|---|---|
| `IpcServer.cs` | Named pipe server (service side), user-only ACL |
| `IpcClient.cs` | Named pipe client (tray side) |
| `IpcMessage.cs` | Message types + payload DTOs |
| `IpcSerializer.cs` | JSON serialization for pipe protocol |

### Service Host (`SyncIt.Service/`)
| File | Purpose |
|---|---|
| `Program.cs` | Service entry point, DI, single-instance mutex |
| `SyncWorker.cs` | BackgroundService orchestrator |

### Tray App (`SyncIt.Tray/`)
| File | Purpose |
|---|---|
| `Program.cs` | Tray entry point |
| `TrayApp.cs` | Main tray application logic |
| `TrayIconManager.cs` | System tray icon state management |
| `StatusForm.cs` | Status display window |
| `FirstRunForm.cs` | First-run OAuth setup wizard |
| `DeletionWarningForm.cs` | Deletion freeze approval dialog |
| `PairPickerDialog.cs` | Sync pair selection |
| `SelectFoldersForm.cs` | Drive folder browser |

---

## Data Flow Documentation

### Flow 1: Local File Created/Modified → Upload to Drive
```
FileSystemWatcher event
  → OnLocalChange() filters (glob, size, AtomicWriter in-progress)
  → ChangeQueue.Enqueue() with 500ms debounce
  → ConsumeChangesAsync() dequeues
  → HandleLocalChangeAsync()
    → FileLockHelper check (requeue if locked)
    → Check remote baseline for conflict
    → UploadLocalFileAsync() via SemaphoreSlim(3)
      → GoogleDriveService.UploadFileAsync() with Polly retry
      → UpsertRecord for both Local + Remote baselines
```

### Flow 2: Remote File Changed → Download Locally
```
PollRemoteLoopAsync() every N seconds
  → GoogleDriveService.ListChangesAsync() (page token delta)
  → HandleRemoteChangeAsync()
    → Skip if HeadRevisionId matches baseline (own upload echo)
    → FastChangeDetector + hash to detect conflict
    → DownloadRemoteFileAsync() via SemaphoreSlim(3)
      → AtomicFileWriter: .tmp → MD5 verify → rename
      → UpsertRecord for both Local + Remote baselines
```

### Flow 3: Local File Deleted → Trash on Drive
```
FSW Deleted event → ChangeQueue → ConsumeChangesAsync()
  → HandleLocalDeleteAsync()
    → Create DeletionRecord (Side=Remote, reason=DeletedLocally)
    → DeletionCoordinator.SubmitAsync()
      → DeletionSafetyValidator.ValidateAsync()
        → Layer 1: verify baseline exists (reject phantom deletes)
        → Layer 2: threshold check (freeze if > MaxDeletionsPerCycle)
      → DeletionJournal.RecordAsync() with grace cycles
  → ExecuteApprovedAsync() (on next poll cycle, after grace)
    → GoogleDriveService.TrashFileAsync() (NOT permanent delete)
```

### Flow 4: Remote File Deleted → Local Trash
```
ListChangesAsync() detects removed/trashed file
  → HandleRemoteDeleteAsync()
    → Stream records to find by DriveFileId
    → Create DeletionRecord (Side=Local, reason=DeletedRemotely)
    → DeletionCoordinator.SubmitAsync() (same pipeline)
  → ExecuteApprovedAsync()
    → LocalTrashManager.MoveToTrash() → .syncit-trash/{pairId}/{timestamp}/
```

### Flow 5: Deletion Freeze → User Approval
```
DeletionSafetyValidator detects > MaxDeletionsPerCycle
  → All deletions marked Frozen in journal
  → DeletionCoordinator raises DeletionFreezeTriggered event
  → SyncWorker sends IPC: DeletionFreezeTriggered to tray
  → Tray shows DeletionWarningForm
  → User clicks Approve → IPC: ApproveDeletions
    → DeletionCoordinator.ApproveFrozenAsync() → set to Pending(grace=0)
  → OR User clicks Cancel → IPC: CancelDeletions
    → DeletionCoordinator.CancelFrozenAsync() → set to Cancelled
```

---

## Bugs Found

### Critical (Data Loss / Crash Risk)

#### BUG-001: FileInfo access not guarded in FSW handler
**File:** `SyncEngine.cs:173`
**Issue:** `new FileInfo(absolutePath).Length` can throw `UnauthorizedAccessException`, `FileNotFoundException`, or `IOException` inside the FSW callback. Since FSW callbacks run on threadpool threads, an unhandled exception here will crash the process.
**Impact:** Service crash on permission-denied or race with file deletion.
**Fix:** Wrap in try-catch, log warning, skip the event.

#### BUG-002: Fire-and-forget Task.Run for consumer/poll loops
**File:** `SyncEngine.cs:115-116`
**Issue:** `_ = Task.Run(() => ConsumeChangesAsync(...))` discards the task. If the consumer or poll loop throws an unhandled exception, it silently dies. The engine continues running but stops processing changes — sync appears to hang silently.
**Impact:** Silent sync failure with no error indication.
**Fix:** Store tasks, observe exceptions, set error state.

#### BUG-003: DisposeAsync doesn't cancel running tasks
**File:** `SyncEngine.cs:815-821`
**Issue:** `DisposeAsync` disposes FSW and semaphore but never cancels the poll loop or consumer task. Those fire-and-forget tasks continue running after disposal, potentially accessing disposed resources.
**Impact:** ObjectDisposedException crashes, resource leaks.
**Fix:** Use a CancellationTokenSource, cancel in DisposeAsync, await task completion.

#### BUG-004: CancellationToken.None in TriggerSync IPC handler
**File:** `SyncWorker.cs:184`
**Issue:** `e.TriggerSyncAsync(CancellationToken.None)` — the sync triggered by user IPC command is uncancellable. If service stops during this sync, it can't be interrupted.
**Impact:** Service hangs on shutdown if an IPC-triggered sync is in progress.
**Fix:** Pass `stoppingToken` instead.

#### BUG-005: ChangeQueue recursive retry on TryAdd race
**File:** `ChangeQueue.cs:49`
**Issue:** If `TryAdd` fails, the code recursively calls `Enqueue(ev)`. Under sustained contention, this creates unbounded stack depth → StackOverflowException.
**Impact:** Process crash under high contention.
**Fix:** Use a loop instead of recursion.

### High (Functional Bugs)

#### BUG-006: Silent exception swallowing in TryMakeRelative
**File:** `SyncEngine.cs:748-752`
**Issue:** `catch { return null; }` silently swallows ALL exceptions from path resolution. If `PathHelper.MakeRelative` has a bug, it's invisible.
**Impact:** Files silently skipped without logging.
**Fix:** Log the exception at Debug level.

#### BUG-007: Null ContentHash comparison for Google Docs
**File:** `ConflictResolver.cs:35-36`
**Issue:** Google Docs exports have no MD5 checksum (`Md5Checksum` is null). The hash comparison guard `local.ContentHash is not null && remote.ContentHash is not null` correctly skips them, but then falls through to `LastWriteWins` which compares mtimes. This is correct but fragile — a Google Doc edit changes the export content without changing the Drive file's `modifiedTime` in a predictable way.
**Impact:** Potential false conflict resolution for Google Docs.
**Fix:** Document as known limitation; consider using revision ID for Google Docs conflict detection.

#### BUG-008: ApproveDeletions/CancelDeletions IPC not wired
**File:** `SyncWorker.cs:188-196`
**Issue:** The `ApproveDeletions` and `CancelDeletions` IPC handlers only log a message — they never call `DeletionCoordinator.ApproveFrozenAsync()` or `CancelFrozenAsync()`. Frozen deletions can never be resolved.
**Impact:** Deletion freeze is permanent until service restart. Critical safety feature is broken.
**Fix:** Call the coordinator methods with the pair ID from the payload.

#### BUG-009: Environment.Exit(0) in Shutdown handler
**File:** `SyncWorker.cs:208`
**Issue:** `Environment.Exit(0)` bypasses all `finally` blocks, `DisposeAsync`, and `IHost.StopAsync`. The service process dies abruptly — databases may not flush, pipes not cleaned up.
**Impact:** Database corruption risk, orphaned pipe handles.
**Fix:** Use `IHostApplicationLifetime.StopApplication()` for graceful shutdown.

#### BUG-010: Multiple FileInfo instantiations for same file
**File:** `SyncEngine.cs:173-175`, `496-498`, `677-679`
**Issue:** `new FileInfo(absolutePath)` is called multiple times for the same file in the same method. Each call hits the filesystem. Between calls, the file state can change (TOCTOU race).
**Impact:** Inconsistent metadata used for decisions; minor performance waste.
**Fix:** Create FileInfo once, reuse.

### Medium (Resource Leaks / Performance)

#### BUG-011: StreamWriter not disposed in HandleConnectionAsync
**File:** `IpcServer.cs:112`
**Issue:** `_writer = new StreamWriter(pipe, leaveOpen: true)` is created but never explicitly disposed. When the connection drops, the StreamWriter is orphaned. The `leaveOpen: true` means the pipe isn't affected, but the writer's internal buffer is leaked.
**Impact:** Minor memory leak per connection cycle.
**Fix:** Dispose writer in finally block.

#### BUG-012: FileStateStore creates new connection per operation
**File:** `FileStateStore.cs:258-267`
**Issue:** Every single DB operation opens a new connection, sets WAL pragma, and closes it. SQLite connection pooling via `Cache=Shared` helps, but the PRAGMA execution overhead on every call is wasteful.
**Impact:** Performance overhead, especially during initial sync with many files.
**Fix:** Consider connection pooling or single long-lived connection with proper locking.

#### BUG-013: Initial sync loads all remote files into memory
**File:** `SyncEngine.cs:326-332`
**Issue:** `InitialSyncAsync` collects ALL remote files into a `Dictionary<string, DriveFile>`. For folders with 100K+ files, this could use significant memory.
**Impact:** Memory pressure on large folders. Acceptable for personal use but violates the "minimal memory" design constraint.
**Fix:** Stream-process or batch in chunks.

#### BUG-014: Trash directory timestamp parsing is fragile
**File:** `LocalTrashManager.cs:70-71`
**Issue:** `dirName.Replace('-', ':')` replaces ALL hyphens including date separators. The timestamp format `2026-02-23T14-30-00Z` becomes `2026:02:23T14:30:00Z` which is not valid ISO 8601. `DateTime.TryParse` may still handle it, but it's unreliable.
**Impact:** Old trash entries may never be pruned.
**Fix:** Parse the known format explicitly with `DateTime.TryParseExact`.

#### BUG-015: RetryHelper retries TaskCanceledException
**File:** `RetryHelper.cs:25`
**Issue:** `.Or<TaskCanceledException>()` will retry user-initiated cancellations (e.g., service shutdown). When the user stops the service, every in-flight Drive operation gets retried 5 times before finally terminating.
**Impact:** 30+ second delay on service shutdown.
**Fix:** Only retry `TaskCanceledException` when the inner `CancellationToken` was NOT the one we control.

#### BUG-016: ConfigWatcher.TryReload runs on Timer thread
**File:** `ConfigWatcher.cs:61`
**Issue:** `TryReload()` fires on a Timer callback (threadpool thread). The `ConfigReloaded` event handler in `SyncWorker` is an `async` lambda that calls `StopEnginesAsync()` and `StartEnginesAsync()`. The `async void` lambda means exceptions are unobserved.
**Impact:** If engine restart fails during config reload, the error is swallowed silently.
**Fix:** Make the event handler properly async-aware or use a channel/queue pattern.

### Low (Code Quality)

#### BUG-017: _paused not volatile or thread-safe
**File:** `SyncEngine.cs:41`
**Issue:** `_paused` is a plain `bool` read/written from multiple threads (FSW callbacks, consumer task, poll task, IPC thread). Without `volatile` or `Interlocked`, threads may read stale values.
**Impact:** Pause/resume may take one extra cycle to take effect.
**Fix:** Use `volatile bool` or `Interlocked`.

#### BUG-018: StatusChanged event not null-checked thread-safely
**File:** `SyncEngine.cs:804`
**Issue:** `StatusChanged?.Invoke(...)` — the delegate could become null between the null check and the invoke in a race condition (classic C# event pattern issue).
**Impact:** Extremely rare NullReferenceException.
**Fix:** Capture delegate in local variable first.

#### BUG-019: OnDeletionFreezeTriggered fire-and-forget
**File:** `SyncWorker.cs:269`
**Issue:** `_ = Task.Run(() => _ipcServer?.SendAsync(...))` discards the task. If IPC send fails, the tray never learns about the freeze.
**Impact:** User is unaware of deletion freeze — files stay frozen indefinitely.
**Fix:** Observe the task, log failures.

---

## Bugs Found During End-to-End Testing

### Critical

#### BUG-020: Drive trash not detected by Changes API handler
**File:** `SyncEngine.cs` (RunSyncCycleAsync)
**Issue:** The Changes API handler only called `HandleRemoteDeleteAsync` when `change.Removed == true`. But when a file is trashed on Drive (not permanently deleted), `Removed` is `false`/`null` and `File.Trashed == true`. Trashed files were silently ignored.
**Impact:** Files trashed on Drive were never propagated locally — remote deletes completely broken.
**Fix:** Changed condition to: `change.File is null || change.File.Trashed == true || change.Removed == true`.

#### BUG-021: DeletionCoordinator local trash move triggers FSW echo
**File:** `DeletionCoordinator.cs` (ExecuteSingleDeletionAsync)
**Issue:** When `LocalTrashManager.MoveToTrash()` moves a file, the FSW detects the disappearance and queues a new ChangeEvent (Deleted). This causes a secondary remote deletion of the already-trashed Drive file — a wasteful round-trip echo.
**Impact:** Every remote-triggered local trash move also trashed the Drive file again (already trashed, so harmless but wasteful).
**Fix:** Added `AtomicFileWriter.SuppressPath()` method; DeletionCoordinator calls it before the trash move to suppress FSW events.

#### BUG-022: Deletion freeze only froze new arrivals, not existing Pending
**File:** `DeletionCoordinator.cs` / `DeletionSafetyValidator.cs`
**Issue:** When deletions are submitted one-at-a-time (which is the normal flow), the validator only checked the current batch size (always 1) against the threshold. The first 10 deletions were approved as Pending and would execute after their grace period, even though the total exceeded the threshold. Additionally, when the freeze triggered on the 11th deletion, it only froze the new arrival — the first 10 Pending deletions continued to execute.
**Impact:** Deletion freeze was effectively bypassed for the first N deletions (where N = MaxDeletionsPerCycle). Critical safety feature partially broken.
**Fix:** (1) Validator now queries `DeletionJournal.CountPendingAsync()` to include already-pending deletions in the threshold check. (2) `DeletionCoordinator` calls `DeletionJournal.FreezeAllPendingAsync()` to retroactively freeze all existing Pending deletions when a freeze triggers.

### High

#### BUG-023: HandleLocalChangeAsync update path missing local baseline update
**File:** `SyncEngine.cs` (~line 454)
**Issue:** When a locally-modified file was uploaded to Drive (update path, not create), the code updated the Remote baseline but NOT the Local baseline. On the next change detection cycle, the local file's new mtime/hash didn't match the stale Local baseline, causing the file to be re-uploaded.
**Impact:** Modified files were uploaded twice on every edit.
**Fix:** Added `UpsertRecordAsync` for the Local side after upload, using the updated `FileInfo` metadata.

---

## End-to-End Test Results

| # | Test | Result | Notes |
|---|---|---|---|
| 1 | Local→Drive: create file | PASS | FSW detected, uploaded within 5s |
| 2 | Local→Drive: modify file | PASS (after BUG-023 fix) | Update path now updates both baselines |
| 3 | Local→Drive: rename file | PASS | Old file trashed, new file uploaded |
| 4 | Local→Drive: delete file | PASS | Grace period (2 cycles), then Drive trash |
| 5 | Drive→Local: add file | PASS | Poll detected, atomic download |
| 6 | Drive→Local: modify file | PASS | Atomic download, baseline updated |
| 7 | Drive→Local: delete file | PASS (after BUG-020, BUG-021 fixes) | Local trash + no echo re-deletion |
| 8 | Deletion freeze (>10 files) | PASS (after BUG-022 fix) | All 12 frozen, 0 Drive files trashed |

---

## Stability Test Results

| # | Test | Result | Notes |
|---|---|---|---|
| 9 | Rapid file creation (20 files) | PASS | All 20 uploaded — 15 in first 30s, all 20 after extended wait. Debounce + SemaphoreSlim(3) working as designed |
| 10 | 50MB file upload | PASS | Uploaded successfully with progress tracking. Echo detection prevented re-download |
| 11 | Memory footprint | PASS | Tray ~66MB, Service ~101MB after 50MB upload. Healthy for .NET 8 |
| 12 | Process lifecycle (Exit menu) | PASS | Tray sends IPC Shutdown, service exits cleanly, tray exits |
| 13 | Service auto-restart | PASS | Service killed externally → tray detected exit → auto-restarted after 5s |
| 14 | Overlay DB population | PASS | overlay-state.db grew to 16KB after initial sync (baseline files set to Synced) |
