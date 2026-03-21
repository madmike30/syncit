using Google.Apis.Drive.v3.Data;
using Microsoft.Extensions.Logging;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using IOFile = System.IO.File;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;

namespace SyncIt.Core.Services;

/// <summary>
/// Core bidirectional sync engine for a single SyncPair.
///
/// Lifecycle per pair:
///   StartAsync() → startup validation → (initial sync if no baseline) →
///   FSW listening + poll loop → change queue processing → sync cycles
///
/// Change detection strategy:
///   Local: FileSystemWatcher → ChangeQueue (debounced 500ms)
///   Remote: Drive Changes API polling every N seconds
///   Both use the same ChangeQueue — processed by a single consumer loop
///
/// Delete safety: all deletes go through DeletionCoordinator, never directly.
/// </summary>
public sealed class SyncEngine : ISyncEngine
{
    private readonly SyncPair _pair;
    private readonly SyncConfig _config;
    private readonly IGoogleDriveService _driveService;
    private readonly IFileStateStore _stateStore;
    private readonly IConflictResolver _conflictResolver;
    private readonly IDeletionCoordinator _deletionCoordinator;
    private readonly IGlobFilter _globFilter;
    private readonly IChangeQueue _changeQueue;
    private readonly DeletionSafetyValidator _safetyValidator;
    private readonly AtomicFileWriter _atomicWriter;
    private readonly IOverlayStateStore? _overlayStore;
    private readonly IActivityLog? _activityLog;
    private readonly FailedOperationTracker? _retryTracker;
    private readonly SyncMetrics _metrics = new();
    private readonly ILogger<SyncEngine> _logger;

    private FileSystemWatcher? _fsw;
    private CancellationTokenSource? _internalCts;
    private Task? _consumerTask;
    private Task? _pollTask;
    private SyncState _state = SyncState.Idle;
    private volatile bool _paused;
    private string? _lastError;
    private int _filesUploaded, _filesDownloaded, _conflictsResolved, _errors;

    // Semaphore: max 3 concurrent transfers
    private readonly SemaphoreSlim _transferSlim = new(3, 3);

    /// <summary>Per-file transfer progress tracker — subscribe for IPC push.</summary>
    public TransferTracker TransferTracker { get; } = new();

    /// <summary>Accumulated sync metrics for this engine.</summary>
    public SyncMetrics Metrics => _metrics;

    public event Action<SyncPairStatus>? StatusChanged;

    public SyncEngine(
        SyncPair pair,
        SyncConfig config,
        IGoogleDriveService driveService,
        IFileStateStore stateStore,
        IConflictResolver conflictResolver,
        IDeletionCoordinator deletionCoordinator,
        IGlobFilter globFilter,
        IChangeQueue changeQueue,
        DeletionSafetyValidator safetyValidator,
        AtomicFileWriter atomicWriter,
        ILogger<SyncEngine> logger,
        IOverlayStateStore? overlayStore = null,
        IActivityLog? activityLog = null,
        FailedOperationTracker? retryTracker = null)
    {
        _pair                = pair;
        _config              = config;
        _driveService        = driveService;
        _stateStore          = stateStore;
        _conflictResolver    = conflictResolver;
        _deletionCoordinator = deletionCoordinator;
        _globFilter          = globFilter;
        _changeQueue         = changeQueue;
        _safetyValidator     = safetyValidator;
        _atomicWriter        = atomicWriter;
        _overlayStore        = overlayStore;
        _activityLog         = activityLog;
        _retryTracker        = retryTracker;
        _logger              = logger;
    }

    // -------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------

    public async Task StartAsync(SyncPair pair, CancellationToken ct)
    {
        _logger.LogInformation("Starting sync engine for pair '{Id}': {Local} ↔ {Remote}",
            pair.Id, pair.LocalPath, pair.RemoteFolderName);

        // Step 1: Startup safety validation
        SetState(SyncState.Syncing, "Validating...");
        var check = await _safetyValidator.CheckLocalFolderAsync(pair, ct);
        if (!check.IsOk)
        {
            SetState(SyncState.SafetyHalt, check.ErrorMessage);
            _logger.LogError("Startup check failed for pair {Id}: {Msg}", pair.Id, check.ErrorMessage);
            return;
        }

        // Step 1b: Re-notify about any frozen deletions from previous sessions.
        // Don't auto-cancel — let the user decide. Re-trigger the freeze event
        // so the UI can show the approval dialog again.
        await _deletionCoordinator.RenotifyFrozenAsync(pair.Id, ct);

        // Step 2: Initialize Drive page token (for remote change polling)
        await EnsurePageTokenAsync(pair, ct);

        // Step 3: Set up FileSystemWatcher
        SetupFileSystemWatcher(pair);

        // Step 4: Initial sync if no baseline exists
        var baselineCount = await _stateStore.CountRecordsAsync(pair.Id, SyncSide.Local);
        if (baselineCount == 0)
        {
            SetState(SyncState.Syncing, "Initial sync...");
            await InitialSyncAsync(pair, ct);
        }

        SetState(SyncState.Idle);

        // Step 5: Start the change consumer and poll loop concurrently
        // Link the external ct with our internal CTS so DisposeAsync can cancel both
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _internalCts.Token;

        _consumerTask = Task.Run(async () =>
        {
            try { await ConsumeChangesAsync(pair, linkedCt); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer loop crashed for pair {Id}", pair.Id);
                SetState(SyncState.Error, ex.Message);
            }
        }, linkedCt);

        _pollTask = Task.Run(async () =>
        {
            try { await PollRemoteLoopAsync(pair, linkedCt); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll loop crashed for pair {Id}", pair.Id);
                SetState(SyncState.Error, ex.Message);
            }
        }, linkedCt);

        _logger.LogInformation("Sync engine started for pair '{Id}'.", pair.Id);
    }

    public void Pause()  { _paused = true;  SetState(SyncState.Paused); }
    public void Resume() { _paused = false; SetState(SyncState.Idle); }

    public async Task TriggerSyncAsync(CancellationToken ct)
    {
        await RunSyncCycleAsync(_pair, ct);
    }

    public SyncPairStatus GetStatus() => new()
    {
        SyncPairId       = _pair.Id,
        LocalPath        = _pair.LocalPath,
        RemoteFolderName = _pair.RemoteFolderName,
        State            = _state,
        PendingChanges   = _changeQueue.ReadyCount + _changeQueue.PendingCount,
        FailedOperations = _retryTracker?.FailedCount ?? 0,
        ExhaustedRetries = _retryTracker?.ExhaustedCount ?? 0,
        LastError        = _lastError,
    };

    // -------------------------------------------------------------------------
    // FileSystemWatcher
    // -------------------------------------------------------------------------

    private void SetupFileSystemWatcher(SyncPair pair)
    {
        PathHelper.EnsureDirectory(pair.LocalPath);

        _fsw = new FileSystemWatcher(pair.LocalPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize    = 65536,
            EnableRaisingEvents   = true,
        };

        _fsw.Created += (_, e) => OnLocalChange(pair, ChangeKind.Created,  e.FullPath);
        _fsw.Changed += (_, e) => OnLocalChange(pair, ChangeKind.Modified, e.FullPath);
        _fsw.Deleted += (_, e) => OnLocalChange(pair, ChangeKind.Deleted,  e.FullPath);
        _fsw.Renamed += (_, e) => OnLocalRename(pair, e.OldFullPath, e.FullPath);
        _fsw.Error   += (_, e) => _logger.LogWarning(e.GetException(), "FSW error for pair {Id}", pair.Id);

        _logger.LogDebug("FileSystemWatcher set up for {Path}", pair.LocalPath);
    }

    private void OnLocalChange(SyncPair pair, ChangeKind kind, string absolutePath)
    {
        // Skip if this write was triggered by us (atomic download)
        if (_atomicWriter.IsInProgress(absolutePath)) return;

        var relativePath = TryMakeRelative(pair.LocalPath, absolutePath);
        if (relativePath is null) return;

        if (!_globFilter.IsIncluded(relativePath, pair)) return;
        if (kind != ChangeKind.Deleted)
        {
            try
            {
                var fi = new FileInfo(absolutePath);
                if (fi.Length > pair.MaxFileSizeBytes)
                {
                    _logger.LogWarning("Skipping oversized file: {Path} ({Size:N0} bytes)", relativePath, fi.Length);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                _logger.LogDebug(ex, "Cannot stat file '{Path}' in FSW handler — skipping event.", relativePath);
                return;
            }
        }

        // Mark file as pending in overlay (fire-and-forget; FSW callback must not block)
        if (_overlayStore is not null)
            _ = SetOverlayAsync(relativePath, OverlaySyncState.Pending);

        _changeQueue.Enqueue(new ChangeEvent
        {
            SyncPairId   = pair.Id,
            Side         = SyncSide.Local,
            Kind         = kind,
            RelativePath = relativePath,
        });
    }

    private void OnLocalRename(SyncPair pair, string oldAbsolutePath, string newAbsolutePath)
    {
        if (_atomicWriter.IsInProgress(newAbsolutePath)) return;

        var oldRel = TryMakeRelative(pair.LocalPath, oldAbsolutePath);
        var newRel = TryMakeRelative(pair.LocalPath, newAbsolutePath);
        if (oldRel is null || newRel is null) return;

        _changeQueue.Enqueue(new ChangeEvent
        {
            SyncPairId      = pair.Id,
            Side            = SyncSide.Local,
            Kind            = ChangeKind.Renamed,
            RelativePath    = newRel,
            OldRelativePath = oldRel,
        });
    }

    // -------------------------------------------------------------------------
    // Remote polling
    // -------------------------------------------------------------------------

    private async Task PollRemoteLoopAsync(SyncPair pair, CancellationToken ct)
    {
        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);

                if (_paused) continue;

                await RunSyncCycleAsync(pair, ct);
                consecutiveErrors = 0;

                // After a successful sync cycle, re-enqueue any retryable failed operations
                if (_retryTracker is not null)
                {
                    var retryable = _retryTracker.GetRetryableEvents();
                    foreach (var ev in retryable)
                    {
                        _logger.LogDebug("Retrying failed operation: {Kind} '{Path}'", ev.Kind, ev.RelativePath);
                        _changeQueue.Enqueue(ev);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogError(ex, "Error in poll loop for pair {Id}", pair.Id);
                _lastError = ex.Message;
                SetState(SyncState.Error);

                // Exponential backoff: base * 2^(errors-1), capped at maxDelay
                var backoffSec = _config.RetryBaseDelaySeconds * Math.Pow(2, consecutiveErrors - 1);
                backoffSec = Math.Min(backoffSec, _config.RetryMaxDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Change consumer
    // -------------------------------------------------------------------------

    private async Task ConsumeChangesAsync(SyncPair pair, CancellationToken ct)
    {
        await foreach (var change in _changeQueue.ReadAllAsync(ct))
        {
            // While paused, wait instead of dropping events
            while (_paused && !ct.IsCancellationRequested)
                await Task.Delay(500, ct);

            try
            {
                SetState(SyncState.Syncing, $"{change.Kind}: {change.RelativePath}");

                if (change.Kind == ChangeKind.Deleted)
                {
                    await HandleLocalDeleteAsync(change, pair, ct);
                }
                else
                {
                    await HandleLocalChangeAsync(change, pair, ct);
                }

                // Clear from retry tracker on success
                _retryTracker?.MarkSuccess(change);
            }
            catch (Exception ex)
            {
                _errors++;
                _metrics.RecordError();
                _lastError = ex.Message;
                LogActivity(ActivityType.Error, change.RelativePath, details: ex.Message);
                _logger.LogError(ex, "Error processing change {Kind} '{Path}'", change.Kind, change.RelativePath);

                // Record failure for retry (if tracker available and not exhausted)
                if (_retryTracker is not null && !_retryTracker.IsExhausted(change))
                    _retryTracker.RecordFailure(change, ex.Message);

                SetState(SyncState.Error);
            }
            finally
            {
                SetState(SyncState.Idle);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Sync cycle (called by poll loop and TriggerSyncAsync)
    // -------------------------------------------------------------------------

    private async Task RunSyncCycleAsync(SyncPair pair, CancellationToken ct)
    {
        SetState(SyncState.Syncing, "Checking for remote changes...");
        _metrics.StartCycle();

        try
        {
            var pageToken = await _stateStore.GetPageTokenAsync(pair.Id);
            if (pageToken is null) return; // not initialized yet

            var (changes, newToken) = await _driveService.ListChangesAsync(pageToken, ct);
            await _stateStore.SavePageTokenAsync(pair.Id, newToken);

            foreach (var change in changes)
            {
                // Handle deletions: file trashed on Drive, permanently deleted, or removed from our view
                if (change.File is null || change.File.Trashed == true || change.Removed == true)
                {
                    if (change.FileId is not null)
                        await HandleRemoteDeleteAsync(change.FileId, pair, ct);
                    continue;
                }

                var relativePath = await _driveService.GetRelativePathAsync(change.FileId, pair.RemoteFolderId, ct);
                if (relativePath is null) continue;
                if (!_globFilter.IsIncluded(relativePath, pair)) continue;

                await HandleRemoteChangeAsync(change.File, relativePath, pair, ct);
            }

            // Execute any approved deletions
            await _deletionCoordinator.ExecuteApprovedAsync(pair, ct);

            await _stateStore.SetLastSyncTimeAsync(pair.Id, DateTime.UtcNow);
            _metrics.EndCycle();
            SetState(SyncState.Idle);
        }
        catch (Exception ex)
        {
            _metrics.EndCycle();
            _errors++;
            _metrics.RecordError();
            _lastError = ex.Message;
            LogActivity(ActivityType.Error, "", details: $"Sync cycle failed: {ex.Message}");
            _logger.LogError(ex, "Sync cycle failed for pair {Id}", pair.Id);
            SetState(SyncState.Error);
        }
    }

    // -------------------------------------------------------------------------
    // Initial sync
    // -------------------------------------------------------------------------

    private async Task InitialSyncAsync(SyncPair pair, CancellationToken ct)
    {
        _logger.LogInformation("Running initial sync for pair '{Id}'...", pair.Id);

        // Collect remote files into a dictionary for O(1) lookup
        var remoteFiles = new Dictionary<string, Google.Apis.Drive.v3.Data.File>(StringComparer.OrdinalIgnoreCase);
        await foreach (var file in _driveService.ListFilesAsync(pair.RemoteFolderId, ct))
        {
            var rel = await _driveService.GetRelativePathAsync(file.Id, pair.RemoteFolderId, ct);
            if (rel is not null && _globFilter.IsIncluded(rel, pair))
                remoteFiles[rel] = file;
        }

        // Process local files
        var localFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var absPath in Directory.EnumerateFiles(pair.LocalPath, "*", SearchOption.AllDirectories))
        {
            var rel = TryMakeRelative(pair.LocalPath, absPath);
            if (rel is null || !_globFilter.IsIncluded(rel, pair)) continue;
            if (new FileInfo(absPath).Length > pair.MaxFileSizeBytes) continue;
            localFiles.Add(rel);

            if (remoteFiles.TryGetValue(rel, out var remoteFile))
            {
                // Both exist — check for conflict
                await HandleBothExistAsync(rel, absPath, remoteFile, pair, ct);
            }
            else
            {
                // Local only → upload
                await UploadLocalFileAsync(absPath, rel, pair, ct);
            }
        }

        // Remote files not in local → download
        foreach (var (rel, remoteFile) in remoteFiles)
        {
            if (!localFiles.Contains(rel))
                await DownloadRemoteFileAsync(remoteFile, rel, pair, ct);
        }

        // Batch-set all synced files to Synced overlay state
        if (_overlayStore is not null)
        {
            try
            {
                var overlayItems = new List<(string, OverlaySyncState)>();
                foreach (var absPath in Directory.EnumerateFiles(pair.LocalPath, "*", SearchOption.AllDirectories))
                {
                    var rel = TryMakeRelative(pair.LocalPath, absPath);
                    if (rel is not null && _globFilter.IsIncluded(rel, pair))
                        overlayItems.Add((absPath, OverlaySyncState.Synced));
                }
                if (overlayItems.Count > 0)
                    await _overlayStore.SetBatchAsync(overlayItems, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set initial overlay state for pair {Id}", pair.Id);
            }
        }

        _logger.LogInformation("Initial sync complete for pair '{Id}'.", pair.Id);
    }

    // -------------------------------------------------------------------------
    // Local change handlers
    // -------------------------------------------------------------------------

    private async Task HandleLocalChangeAsync(ChangeEvent ev, SyncPair pair, CancellationToken ct)
    {
        var absolutePath = PathHelper.ToAbsolute(pair.LocalPath, ev.RelativePath);

        // If file was deleted while we were waiting, treat as a delete
        if (!IOFile.Exists(absolutePath) && ev.Kind != ChangeKind.Deleted)
        {
            _logger.LogDebug("File vanished before processing: {Path} — treating as delete.", ev.RelativePath);
            await HandleLocalDeleteAsync(ev with { Kind = ChangeKind.Deleted }, pair, ct);
            return;
        }

        if (IOFile.Exists(absolutePath) && FileLockHelper.IsLocked(absolutePath) && ev.RetryCount < FileLockHelper.MaxRetries)
        {
            _logger.LogDebug("File locked: {Path} — requeueing (attempt {N})", ev.RelativePath, ev.RetryCount + 1);
            await Task.Delay(FileLockHelper.RetryDelay(ev.RetryCount), ct);
            _changeQueue.Enqueue(ev with { RetryCount = ev.RetryCount + 1 });
            return;
        }

        var baseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.RelativePath);

        if (ev.Kind == ChangeKind.Renamed && ev.OldRelativePath is not null)
        {
            // Upload new path first (safe order: create before delete)
            var oldBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath);
            await UploadLocalFileAsync(absolutePath, ev.RelativePath, pair, ct); // updates both baselines

            // Only trash old Drive file after upload succeeds — prevents data loss on failure
            if (oldBaseline?.DriveFileId is not null)
                await _driveService.TrashFileAsync(oldBaseline.DriveFileId, ct);

            // Clean up old path baselines
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath);
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Local, ev.OldRelativePath);
        }
        else if (baseline?.DriveFileId is not null)
        {
            // Existing Drive file — check for conflict
            var localRecord = await BuildLocalRecordAsync(pair.Id, ev.RelativePath, absolutePath);
            if (localRecord is null) return;

            if (baseline.DriveRevisionId is not null &&
                !string.Equals(baseline.ContentHash, localRecord.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                // Both sides may have changed — check conflict
                var localBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, ev.RelativePath);
                if (localBaseline is not null &&
                    !string.Equals(localRecord.ContentHash, localBaseline.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Local changed since baseline — check if remote also changed
                    var remoteFile = await _driveService.GetFileAsync(baseline.DriveFileId, ct);
                    if (remoteFile is not null && remoteFile.HeadRevisionId != baseline.DriveRevisionId)
                    {
                        await ResolveConflictAsync(localRecord, remoteFile, ev.RelativePath, pair, ct);
                        return;
                    }
                }
            }

            // No conflict — update Drive content (throttle with semaphore like uploads/downloads)
            await _transferSlim.WaitAsync(ct);
            try
            {
                var fileSize = new FileInfo(absolutePath).Length;
                var progress = TransferTracker.Start(pair.Id, ev.RelativePath, TransferDirection.Upload, fileSize);
                await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Syncing, ct);

                var mimeType = MimeTypeFor(ev.RelativePath);
                var updated  = await _driveService.UpdateFileAsync(baseline.DriveFileId, absolutePath, mimeType, progress, ct);
                await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, ev.RelativePath, updated));

                // Update local baseline so next change detection works correctly
                var updatedLocalMeta = new FileInfo(absolutePath);
                await _stateStore.UpsertRecordAsync(new FileRecord
                {
                    SyncPairId   = pair.Id,
                    Side         = SyncSide.Local,
                    RelativePath = ev.RelativePath,
                    SizeBytes    = updatedLocalMeta.Length,
                    ModifiedUtc  = updatedLocalMeta.LastWriteTimeUtc,
                    ContentHash  = updated.Md5Checksum,
                });

                TransferTracker.Complete(pair.Id, ev.RelativePath);
                await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Synced, ct);
                _filesUploaded++;
                _metrics.RecordUpload(updatedLocalMeta.Length);
                LogActivity(ActivityType.Upload, ev.RelativePath, updatedLocalMeta.Length);
                _logger.LogInformation("Updated: {Path}", ev.RelativePath);
            }
            catch (Exception ex)
            {
                TransferTracker.Fail(pair.Id, ev.RelativePath, ex.Message);
                await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Error);
                throw;
            }
            finally { _transferSlim.Release(); }
        }
        else
        {
            // New file — upload
            await UploadLocalFileAsync(absolutePath, ev.RelativePath, pair, ct); // manages semaphore internally
        }
    }

    private async Task HandleLocalDeleteAsync(ChangeEvent ev, SyncPair pair, CancellationToken ct)
    {
        var baseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.RelativePath);
        if (baseline is null)
        {
            _logger.LogDebug("Local delete of '{Path}' has no remote baseline — nothing to propagate.", ev.RelativePath);
            return;
        }

        var deletion = new DeletionRecord
        {
            SyncPairId    = pair.Id,
            RelativePath  = ev.RelativePath,
            Side          = SyncSide.Remote,  // we're deleting the REMOTE copy
            DriveFileId   = baseline.DriveFileId,
            SizeBytes     = baseline.SizeBytes,
            ModifiedUtc   = baseline.ModifiedUtc,
            TriggerReason = "DeletedLocally",
        };

        await _deletionCoordinator.SubmitAsync(pair, new[] { deletion }, ct);

        // Remove overlay state for deleted file
        if (_overlayStore is not null)
        {
            try
            {
                var absPath = PathHelper.ToAbsolute(pair.LocalPath, ev.RelativePath);
                await _overlayStore.RemoveAsync(absPath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to remove overlay state for deleted '{Path}'", ev.RelativePath);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Remote change handlers
    // -------------------------------------------------------------------------

    private async Task HandleRemoteChangeAsync(
        Google.Apis.Drive.v3.Data.File file,
        string relativePath,
        SyncPair pair,
        CancellationToken ct)
    {
        var localAbsPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);
        var localBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, relativePath);
        var remoteBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, relativePath);

        // Skip if the Drive revision matches our baseline — this is our own upload being
        // echoed back by the Changes API. No action needed; avoids unnecessary re-downloads
        // that can trigger FSW events and (in rare races) duplicate uploads.
        if (remoteBaseline is not null
            && !string.IsNullOrEmpty(remoteBaseline.DriveRevisionId)
            && remoteBaseline.DriveRevisionId == file.HeadRevisionId)
        {
            _logger.LogDebug("Skipping remote change for '{Path}' — revision unchanged (own upload echo).", relativePath);
            return;
        }

        // Detect remote rename: if the DriveFileId exists in our baseline under a different
        // path, this is a rename/move. Clean up the old local file and baselines.
        if (remoteBaseline is null && file.Id is not null)
        {
            FileRecord? oldRemote = null;
            await foreach (var rec in _stateStore.StreamRecordsAsync(pair.Id, SyncSide.Remote, ct))
            {
                if (rec.DriveFileId == file.Id) { oldRemote = rec; break; }
            }

            if (oldRemote is not null && !string.Equals(oldRemote.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Remote rename detected: '{Old}' → '{New}'", oldRemote.RelativePath, relativePath);
                var oldLocalPath = PathHelper.ToAbsolute(pair.LocalPath, oldRemote.RelativePath);
                if (IOFile.Exists(oldLocalPath))
                {
                    _atomicWriter.SuppressPath(oldLocalPath);
                    IOFile.Delete(oldLocalPath);
                }
                await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Remote, oldRemote.RelativePath);
                await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Local, oldRemote.RelativePath);
            }
        }

        // Check if local also changed since baseline → conflict
        if (localBaseline is not null && IOFile.Exists(localAbsPath))
        {
            var detectResult = FastChangeDetector.CheckLocalFile(localAbsPath, localBaseline);
            if (detectResult == ChangeResult.NeedsHash)
            {
                var localHash = await HashHelper.ComputeMd5Async(localAbsPath, ct);
                if (localHash != localBaseline.ContentHash)
                {
                    // Both local and remote changed → conflict
                    var localInfo = new FileInfo(localAbsPath);
                    var localRecord = new FileRecord
                    {
                        SyncPairId   = pair.Id,
                        Side         = SyncSide.Local,
                        RelativePath = relativePath,
                        ContentHash  = localHash,
                        ModifiedUtc  = localInfo.LastWriteTimeUtc,
                        SizeBytes    = localInfo.Length,
                    };
                    await ResolveConflictAsync(localRecord, file, relativePath, pair, ct);
                    return;
                }
            }
        }

        // No conflict — download
        await DownloadRemoteFileAsync(file, relativePath, pair, ct);
    }

    private async Task HandleRemoteDeleteAsync(string driveFileId, SyncPair pair, CancellationToken ct)
    {
        // Find by driveFileId since we only have the ID, not the path
        FileRecord? found = null;
        await foreach (var rec in _stateStore.StreamRecordsAsync(pair.Id, SyncSide.Remote, ct))
        {
            if (rec.DriveFileId == driveFileId) { found = rec; break; }
        }
        if (found is null) return;

        // Check if local file was modified since our baseline — if so, keep it (local wins)
        var localAbsPath = PathHelper.ToAbsolute(pair.LocalPath, found.RelativePath);
        var localBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, found.RelativePath);
        if (localBaseline is not null && IOFile.Exists(localAbsPath))
        {
            var detectResult = FastChangeDetector.CheckLocalFile(localAbsPath, localBaseline);
            if (detectResult == ChangeResult.NeedsHash)
            {
                var localHash = await HashHelper.ComputeMd5Async(localAbsPath, ct);
                if (localHash != localBaseline.ContentHash)
                {
                    // Local file was modified but remote deleted — keep local, re-upload
                    _logger.LogWarning(
                        "Remote deleted '{Path}' but local was modified — keeping local copy and re-uploading.",
                        found.RelativePath);
                    await UploadLocalFileAsync(localAbsPath, found.RelativePath, pair, ct);
                    return;
                }
            }
        }

        var deletion = new DeletionRecord
        {
            SyncPairId    = pair.Id,
            RelativePath  = found.RelativePath,
            Side          = SyncSide.Local,   // we're deleting the LOCAL copy
            SizeBytes     = found.SizeBytes,
            ModifiedUtc   = found.ModifiedUtc,
            TriggerReason = "DeletedRemotely",
        };

        await _deletionCoordinator.SubmitAsync(pair, new[] { deletion }, ct);
    }

    // -------------------------------------------------------------------------
    // Upload / Download
    // -------------------------------------------------------------------------

    private async Task UploadLocalFileAsync(string absolutePath, string relativePath, SyncPair pair, CancellationToken ct)
    {
        if (!IOFile.Exists(absolutePath)) return;

        var fileName   = Path.GetFileName(relativePath);
        var parentRel  = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var parentId   = parentRel.Length > 0
            ? await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, parentRel, ct)
            : pair.RemoteFolderId;

        await _transferSlim.WaitAsync(ct);
        try
        {
            var fileSize = new FileInfo(absolutePath).Length;
            var progress = TransferTracker.Start(pair.Id, relativePath, TransferDirection.Upload, fileSize);
            await SetOverlayAsync(relativePath, OverlaySyncState.Syncing, ct);

            var mimeType   = MimeTypeFor(relativePath);
            var uploaded   = await _driveService.UploadFileAsync(absolutePath, fileName, parentId, mimeType, progress, ct);
            var localMeta  = new FileInfo(absolutePath);

            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId   = pair.Id,
                Side         = SyncSide.Remote,
                RelativePath = relativePath,
                DriveFileId  = uploaded.Id,
                DriveRevisionId = uploaded.HeadRevisionId,
                SizeBytes    = uploaded.Size ?? 0,
                ModifiedUtc  = uploaded.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                ContentHash  = uploaded.Md5Checksum,
                MimeType     = uploaded.MimeType,
            });
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId   = pair.Id,
                Side         = SyncSide.Local,
                RelativePath = relativePath,
                SizeBytes    = localMeta.Length,
                ModifiedUtc  = localMeta.LastWriteTimeUtc,
                ContentHash  = uploaded.Md5Checksum,
            });

            TransferTracker.Complete(pair.Id, relativePath);
            await SetOverlayAsync(relativePath, OverlaySyncState.Synced, ct);
            _filesUploaded++;
            _metrics.RecordUpload(localMeta.Length);
            LogActivity(ActivityType.Upload, relativePath, localMeta.Length);
            _logger.LogInformation("Uploaded: {Path}", relativePath);
        }
        catch (Exception ex)
        {
            TransferTracker.Fail(pair.Id, relativePath, ex.Message);
            await SetOverlayAsync(relativePath, OverlaySyncState.Error);
            throw;
        }
        finally { _transferSlim.Release(); }
    }

    private async Task DownloadRemoteFileAsync(
        Google.Apis.Drive.v3.Data.File file,
        string relativePath,
        SyncPair pair,
        CancellationToken ct)
    {
        var isGoogleAppsType = file.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) == true;
        var isFolder = file.MimeType == "application/vnd.google-apps.folder";

        // Google Apps types that aren't in our export map can never be downloaded —
        // skip them silently (e.g. shortcuts, maps, sites, Jamboard, etc.)
        if (isGoogleAppsType && !GoogleDocExportMap.IsGoogleDoc(file.MimeType) && !isFolder)
        {
            _logger.LogDebug("Skipping non-downloadable Google Apps file: {Path} (MIME: {Mime})", relativePath, file.MimeType);
            return;
        }

        // Folders are handled by EnsureFolderPathAsync, not download
        if (isFolder)
        {
            var folderLocalPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);
            Directory.CreateDirectory(folderLocalPath);
            return;
        }

        var localPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);

        // Determine output path (Google Docs get a different extension)
        string outputPath = localPath;
        string? exportMime = null;

        if (GoogleDocExportMap.TryGetExport(file.MimeType, out exportMime, out var ext))
        {
            if (!pair.ExportGoogleDocs)
            {
                _logger.LogDebug("Skipping Google Doc (export disabled): {Path}", relativePath);
                return;
            }
            outputPath = Path.ChangeExtension(localPath, ext);
        }
        else if (isGoogleAppsType)
        {
            // Safety: any remaining google-apps type without an export mapping can't be downloaded
            _logger.LogDebug("Skipping unknown Google Apps type: {Path} (MIME: {Mime})", relativePath, file.MimeType);
            return;
        }

        await _transferSlim.WaitAsync(ct);
        try
        {
            var totalBytes = file.Size ?? 0;
            var progress = TransferTracker.Start(pair.Id, relativePath, TransferDirection.Download, totalBytes);
            await SetOverlayAsync(relativePath, OverlaySyncState.Syncing, ct);
            string? expectedMd5 = string.IsNullOrEmpty(file.Md5Checksum) ? null : file.Md5Checksum;

            if (!string.IsNullOrEmpty(exportMime))
            {
                // Google Docs export — always small, use MemoryStream
                using var ms = new MemoryStream();
                await _driveService.ExportGoogleDocAsync(file.Id, exportMime, ms, ct);
                ms.Position = 0;
                await _atomicWriter.WriteAsync(ms, outputPath, null, file.ModifiedTimeDateTimeOffset?.UtcDateTime, ct);
            }
            else if (totalBytes > 5 * 1024 * 1024)
            {
                // Large file (>5MB) — stream directly to temp file to avoid heap pressure
                await _atomicWriter.WriteFromCallbackAsync(
                    stream => _driveService.DownloadFileAsync(file.Id, stream, progress, ct),
                    outputPath,
                    expectedMd5,
                    file.ModifiedTimeDateTimeOffset?.UtcDateTime,
                    ct);
            }
            else
            {
                // Small file — use MemoryStream (fast path)
                using var ms = new MemoryStream();
                await _driveService.DownloadFileAsync(file.Id, ms, progress, ct);
                ms.Position = 0;
                await _atomicWriter.WriteAsync(ms, outputPath, expectedMd5, file.ModifiedTimeDateTimeOffset?.UtcDateTime, ct);
            }

            // Update baseline records
            var localInfo = new FileInfo(outputPath);
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId    = pair.Id,
                Side          = SyncSide.Remote,
                RelativePath  = relativePath,
                DriveFileId   = file.Id,
                DriveRevisionId = file.HeadRevisionId,
                SizeBytes     = file.Size ?? 0,
                ModifiedUtc   = file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                ContentHash   = file.Md5Checksum,
                MimeType      = file.MimeType,
            });
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId   = pair.Id,
                Side         = SyncSide.Local,
                RelativePath = relativePath,
                SizeBytes    = localInfo.Length,
                ModifiedUtc  = localInfo.LastWriteTimeUtc,
                ContentHash  = file.Md5Checksum,
            });

            TransferTracker.Complete(pair.Id, relativePath);
            await SetOverlayAsync(relativePath, OverlaySyncState.Synced, ct);
            _filesDownloaded++;
            _metrics.RecordDownload(file.Size ?? 0);
            LogActivity(ActivityType.Download, relativePath, file.Size ?? 0);
            _logger.LogInformation("Downloaded: {Path}", relativePath);
        }
        catch (Exception ex)
        {
            TransferTracker.Fail(pair.Id, relativePath, ex.Message);
            await SetOverlayAsync(relativePath, OverlaySyncState.Error);
            throw;
        }
        finally { _transferSlim.Release(); }
    }

    // -------------------------------------------------------------------------
    // Conflict resolution
    // -------------------------------------------------------------------------

    private async Task HandleBothExistAsync(
        string relativePath,
        string localAbsPath,
        Google.Apis.Drive.v3.Data.File remoteFile,
        SyncPair pair,
        CancellationToken ct)
    {
        var localHash = await HashHelper.ComputeMd5Async(localAbsPath, ct);
        if (localHash.Equals(remoteFile.Md5Checksum ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            // Content identical — just record baseline, no transfer needed
            var localInfo = new FileInfo(localAbsPath);
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
                SizeBytes  = localInfo.Length, ModifiedUtc = localInfo.LastWriteTimeUtc, ContentHash = localHash,
            });
            await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, remoteFile));
            return;
        }

        // Content differs on initial sync — treat as conflict with no baseline
        var conflictInfo = new FileInfo(localAbsPath);
        var localRecord = new FileRecord
        {
            SyncPairId   = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
            ContentHash  = localHash, ModifiedUtc = conflictInfo.LastWriteTimeUtc,
            SizeBytes    = conflictInfo.Length,
        };
        await ResolveConflictAsync(localRecord, remoteFile, relativePath, pair, ct);
    }

    private async Task ResolveConflictAsync(
        FileRecord localRecord,
        Google.Apis.Drive.v3.Data.File remoteFile,
        string relativePath,
        SyncPair pair,
        CancellationToken ct)
    {
        var remoteRecord = BuildRemoteRecord(pair.Id, relativePath, remoteFile);
        var baseline     = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, relativePath);

        var action = _conflictResolver.Resolve(pair.ConflictPolicy, new ConflictContext
        {
            LocalRecord   = localRecord,
            RemoteRecord  = remoteRecord,
            BaselineRecord = baseline,
            RelativePath  = relativePath,
        });

        _logger.LogInformation("Conflict on '{Path}': {Outcome}", relativePath, action.Outcome);

        var localAbsPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);

        switch (action.Outcome)
        {
            case ConflictOutcome.UseLocal:
                await UploadLocalFileAsync(localAbsPath, relativePath, pair, ct);
                break;

            case ConflictOutcome.UseRemote:
                await DownloadRemoteFileAsync(remoteFile, relativePath, pair, ct);
                break;

            case ConflictOutcome.KeepBoth:
                // Keep local as-is; download remote to conflict path
                if (action.KeepBothRemotePath is not null)
                    await DownloadRemoteFileAsync(remoteFile, action.KeepBothRemotePath, pair, ct);
                // Also upload local to ensure Drive has the local version
                await UploadLocalFileAsync(localAbsPath, relativePath, pair, ct);
                break;

            case ConflictOutcome.NoConflict:
                // Update both baselines so future change detection works correctly
                await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, remoteFile));
                await _stateStore.UpsertRecordAsync(localRecord);
                break;
        }

        _conflictsResolved++;
        LogActivity(ActivityType.ConflictResolved, relativePath, details: action.ReasonDescription);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void LogActivity(ActivityType type, string relativePath, long sizeBytes = 0, string? details = null)
    {
        _ = _activityLog?.RecordAsync(new ActivityRecord
        {
            SyncPairId = _pair.Id,
            Type = type,
            RelativePath = relativePath,
            SizeBytes = sizeBytes,
            Details = details,
        });
    }

    private async Task EnsurePageTokenAsync(SyncPair pair, CancellationToken ct)
    {
        var existing = await _stateStore.GetPageTokenAsync(pair.Id);
        if (existing is null)
        {
            var token = await _driveService.GetStartPageTokenAsync(ct);
            await _stateStore.SavePageTokenAsync(pair.Id, token);
            _logger.LogDebug("Initialized Drive page token for pair {Id}", pair.Id);
        }
    }

    private string? TryMakeRelative(string rootPath, string absolutePath)
    {
        try { return PathHelper.MakeRelative(rootPath, absolutePath); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to make relative path from '{Root}' for '{Path}'", rootPath, absolutePath);
            return null;
        }
    }

    private async Task<FileRecord?> BuildLocalRecordAsync(string pairId, string relativePath, string absolutePath)
    {
        if (!IOFile.Exists(absolutePath)) return null;
        var fi   = new FileInfo(absolutePath);
        var hash = await HashHelper.ComputeMd5Async(absolutePath);
        return new FileRecord
        {
            SyncPairId   = pairId,
            Side         = SyncSide.Local,
            RelativePath = relativePath,
            SizeBytes    = fi.Length,
            ModifiedUtc  = fi.LastWriteTimeUtc,
            ContentHash  = hash,
        };
    }

    private static FileRecord BuildRemoteRecord(string pairId, string relativePath, Google.Apis.Drive.v3.Data.File file) =>
        new()
        {
            SyncPairId    = pairId,
            Side          = SyncSide.Remote,
            RelativePath  = relativePath,
            DriveFileId   = file.Id,
            DriveRevisionId = file.HeadRevisionId,
            SizeBytes     = file.Size ?? 0,
            ModifiedUtc   = file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
            ContentHash   = file.Md5Checksum,
            MimeType      = file.MimeType,
        };

    /// <summary>Set overlay state for a file (if overlay store is available).</summary>
    private async Task SetOverlayAsync(string relativePath, OverlaySyncState state, CancellationToken ct = default)
    {
        if (_overlayStore is null) return;
        try
        {
            var absPath = PathHelper.ToAbsolute(_pair.LocalPath, relativePath);
            await _overlayStore.SetStateAsync(absPath, state, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set overlay state for '{Path}'", relativePath);
        }
    }

    private static string MimeTypeFor(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt"  => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".zip"  => "application/zip",
            _       => "application/octet-stream",
        };
    }

    private void SetState(SyncState state, string? activity = null)
    {
        _state = state;
        var handler = StatusChanged;
        handler?.Invoke(new SyncPairStatus
        {
            SyncPairId       = _pair.Id,
            LocalPath        = _pair.LocalPath,
            RemoteFolderName = _pair.RemoteFolderName,
            State            = state,
            PendingChanges   = _changeQueue.ReadyCount + _changeQueue.PendingCount,
            LastError        = activity is not null ? null : _lastError,
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel running consumer/poll tasks
        if (_internalCts is not null)
        {
            await _internalCts.CancelAsync();

            // Wait for tasks to complete (with timeout to avoid hanging)
            var tasks = new List<Task>();
            if (_consumerTask is not null) tasks.Add(_consumerTask);
            if (_pollTask is not null) tasks.Add(_pollTask);
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { _logger.LogDebug(ex, "Timeout waiting for engine tasks to stop."); }
            }

            _internalCts.Dispose();
        }

        _fsw?.Dispose();
        _transferSlim.Dispose();
        if (_changeQueue is IDisposable d) d.Dispose();
    }
}
