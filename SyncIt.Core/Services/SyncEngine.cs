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
    private FileSystemEventHandler? _fswCreated, _fswChanged, _fswDeleted;
    private RenamedEventHandler? _fswRenamed;
    private ErrorEventHandler? _fswError;
    private CancellationTokenSource? _internalCts;
    private Task? _consumerTask;
    private Task? _pollTask;
    private SyncState _state = SyncState.Idle;
    private int _paused; // 0 = running, 1 = paused (accessed via Interlocked/Volatile)
    private string? _lastError;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _consecutiveCycleFailures;

    /// <summary>Consecutive failed remote sync cycles (0 = healthy).</summary>
    public int ConsecutiveCycleFailures => Volatile.Read(ref _consecutiveCycleFailures);

    /// <summary>
    /// Cut the current poll/backoff wait short and run a sync cycle now. Called when the network
    /// comes back or the PC resumes from sleep.
    /// </summary>
    public void WakeUp()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Clock used for active hours (tests replace it).</summary>
    public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;

    private bool _outsideHoursReported;

    /// <summary>True while paused by the user or outside the configured active hours.</summary>
    /// <summary>Volume lookup (swappable in tests).</summary>
    public static IVolumeInfo Volumes { get; set; } = new WindowsVolumeInfo();
    private bool _diskMissingReported;

    private bool IsHeld()
    {
        if (Volatile.Read(ref _paused) == 1) return true;
        if (_localRootMissing) return true;
        if (!RemovableDrives.IsAvailable(_pair, Volumes))
        {
            if (!_diskMissingReported)
            {
                _diskMissingReported = true;
                _logger.LogInformation("Disk for pair {Id} is not connected (volume {Serial}) - sync waits for it.", _pair.Id, _pair.VolumeSerial);
                SetState(SyncState.Paused, "Disk not connected");
            }
            return true;
        }
        if (_diskMissingReported)
        {
            _diskMissingReported = false;
            _logger.LogInformation("Disk for pair {Id} connected - sync resumes.", _pair.Id);
            SetState(SyncState.Idle);
        }
        var active = SyncSchedule.IsActive(_config, LocalNow());
        if (!active && !_outsideHoursReported)
        {
            _outsideHoursReported = true;
            _logger.LogInformation("Outside active hours ({Hours}) - sync for pair {Id} waits.", SyncSchedule.Describe(_config), _pair.Id);
            SetState(SyncState.Paused, "Outside active hours");
        }
        else if (active && _outsideHoursReported)
        {
            _outsideHoursReported = false;
            _logger.LogInformation("Active hours started - sync for pair {Id} resumes.", _pair.Id);
            SetState(SyncState.Idle);
        }
        return !active;
    }

    private async Task WaitForNextCycleAsync(TimeSpan delay, CancellationToken ct)
    {
        // Returns after the delay or as soon as WakeUp() is called.
        await _wake.WaitAsync(delay, ct);
    }
    private int _filesUploaded, _filesDownloaded, _conflictsResolved, _errors;

    // Semaphore: max 3 concurrent transfers
    private readonly SemaphoreSlim _transferSlim = new(3, 3);

    /// <summary>Per-file transfer progress tracker — subscribe for IPC push.</summary>
    public TransferTracker TransferTracker { get; } = new();

    /// <summary>Accumulated sync metrics for this engine.</summary>
    public SyncMetrics Metrics => _metrics;

    /// <summary>How long a changed file must be quiet before upload (shorter in tests).</summary>
    public TimeSpan SettleTime { get; init; } = FileLockHelper.SettleTime;

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
        if (_internalCts is not null)
            throw new InvalidOperationException("Engine is already running. Call DisposeAsync first.");

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

        // Step 4: Validate remote folder access before sync
        try
        {
            var testFile = await _driveService.GetFileAsync(pair.RemoteFolderId, ct);
            if (testFile is null)
            {
                SetState(SyncState.Error, $"Drive folder not accessible (ID: {pair.RemoteFolderId}). Check account and folder config.");
                _logger.LogError("Remote folder {FolderId} not accessible for pair {Id}. Possibly wrong account or deleted folder.", pair.RemoteFolderId, pair.Id);
                return;
            }
        }
        catch (Exception ex)
        {
            SetState(SyncState.Error, $"Cannot access Drive folder: {ex.Message}");
            _logger.LogError(ex, "Failed to access remote folder {FolderId} for pair {Id}", pair.RemoteFolderId, pair.Id);
            return;
        }

        // Step 5: Initial sync if no baseline exists, otherwise reconcile
        var baselineCount = await _stateStore.CountRecordsAsync(pair.Id, SyncSide.Local);
        try
        {
            if (baselineCount == 0)
            {
                SetState(SyncState.Syncing, "Initial sync...");
                await InitialSyncAsync(pair, ct);
            }
            else
            {
                // Reconcile: detect local changes that happened while the engine was stopped.
                // FSW only catches live events — files created/modified/deleted while we were
                // down are invisible without this scan.
                SetState(SyncState.Syncing, "Reconciling...");
                await ReconcileLocalChangesAsync(pair, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial sync/reconcile failed for pair {Id}", pair.Id);
            StopFileSystemWatcher();
            _changeQueue.Clear();
            SetState(SyncState.Error, $"Sync failed: {ex.Message}");
            return;
        }

        SetState(SyncState.Idle);

        // Step 6: Start the change consumer and poll loop concurrently
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

    public void Pause()  { Interlocked.Exchange(ref _paused, 1); SetState(SyncState.Paused); }
    public void Resume() { Interlocked.Exchange(ref _paused, 0); SetState(SyncState.Idle); }

    public async Task TriggerSyncAsync(CancellationToken ct)
    {
        await RunSyncCycleAsync(_pair, ct);
    }

    /// <summary>ID of the pair this engine syncs.</summary>
    public string PairId => _pair.Id;

    public SyncPairStatus GetStatus() => new()
    {
        SyncPairId         = _pair.Id,
        LocalPath          = _pair.LocalPath,
        RemoteFolderName   = _pair.RemoteFolderName,
        State              = _state,
        PendingChanges     = _changeQueue.ReadyCount + _changeQueue.PendingCount,
        FailedOperations   = _retryTracker?.FailedCount ?? 0,
        ExhaustedRetries   = _retryTracker?.ExhaustedCount ?? 0,
        LastError          = _lastError,
        SelectedSubfolders = _pair.SelectedSubfolders,
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

        _fswCreated = (_, e) => OnLocalChange(pair, ChangeKind.Created,  e.FullPath);
        _fswChanged = (_, e) => OnLocalChange(pair, ChangeKind.Modified, e.FullPath);
        _fswDeleted = (_, e) => OnLocalChange(pair, ChangeKind.Deleted,  e.FullPath);
        _fswRenamed = (_, e) => OnLocalRename(pair, e.OldFullPath, e.FullPath);
        _fswError   = (_, e) => OnWatcherError(pair, e.GetException());

        _fsw.Created += _fswCreated;
        _fsw.Changed += _fswChanged;
        _fsw.Deleted += _fswDeleted;
        _fsw.Renamed += _fswRenamed;
        _fsw.Error   += _fswError;

        _logger.LogDebug("FileSystemWatcher set up for {Path}", pair.LocalPath);
    }

    private volatile bool _localRootMissing;

    /// <summary>True while the pair's local root folder does not exist (deleted, drive unplugged).</summary>
    public bool LocalRootMissing => _localRootMissing;

    private void OnWatcherError(SyncPair pair, Exception? ex)
    {
        if (!Directory.Exists(pair.LocalPath))
        {
            if (_localRootMissing) return; // already reported - no error spam
            _localRootMissing = true;
            try { if (_fsw is not null) _fsw.EnableRaisingEvents = false; } catch { }
            _lastError = "Local folder missing";
            _logger.LogWarning("Local folder for pair {Id} is missing ({Path}). Sync is on hold until it comes back.", pair.Id, pair.LocalPath);
            SetState(SyncState.Error);
            _ = Task.Run(() => WaitForLocalRootAsync(pair));
            return;
        }
        _logger.LogWarning(ex, "FSW error for pair {Id}", pair.Id);
    }

    /// <summary>Called by the watcher error path and by tests.</summary>
    internal void CheckLocalRoot() => OnWatcherError(_pair, null);

    private async Task WaitForLocalRootAsync(SyncPair pair)
    {
        while (_localRootMissing)
        {
            try { await Task.Delay(LocalRootRecheck); } catch { return; }
            if (!Directory.Exists(pair.LocalPath)) continue;
            _localRootMissing = false;
            _lastError = null;
            _logger.LogInformation("Local folder for pair {Id} is back; resuming.", pair.Id);
            try
            {
                StopFileSystemWatcher();
                SetupFileSystemWatcher(pair);
            }
            catch (Exception e) { _logger.LogWarning(e, "Could not restart the folder watcher for pair {Id}", pair.Id); }
            SetState(SyncState.Idle);
            WakeUp();
        }
    }

    /// <summary>How often a missing local root is re-checked (tests shorten it).</summary>
    public TimeSpan LocalRootRecheck { get; set; } = TimeSpan.FromSeconds(10);

    private void OnLocalChange(SyncPair pair, ChangeKind kind, string absolutePath)
    {
        // Skip if this write was triggered by us (atomic download)
        if (_atomicWriter.IsInProgress(absolutePath)) return;

        var relativePath = TryMakeRelative(pair.LocalPath, absolutePath);
        if (relativePath is null) return;

        if (!_globFilter.IsIncluded(relativePath, pair)) return;
        if (kind != ChangeKind.Deleted && !Directory.Exists(absolutePath))
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delay = RecoveryBackoff.Next(ConsecutiveCycleFailures, _config.PollIntervalSeconds,
                    Math.Max(_config.RetryMaxDelaySeconds, 60));
                await WaitForNextCycleAsync(delay, ct);

                if (IsHeld()) continue;

                var ok = await RunSyncCycleAsync(pair, ct);

                // After a successful sync cycle, re-enqueue any retryable failed operations
                if (ok && _retryTracker is not null)
                {
                    var retryable = _retryTracker.GetRetryableEvents();
                    foreach (var ev in retryable)
                    {
                        _logger.LogDebug("Retrying failed operation: {Kind} '{Path}'", ev.Kind, ev.RelativePath);
                        _changeQueue.Enqueue(ev);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let the loop die: any unexpected error just counts as a failed cycle.
                Interlocked.Increment(ref _consecutiveCycleFailures);
                _logger.LogError(ex, "Error in poll loop for pair {Id}; will retry.", pair.Id);
                _lastError = ex.Message;
                SetState(SyncState.Error);
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
            while (IsHeld() && !ct.IsCancellationRequested)
                await Task.Delay(500, ct);

            try
            {
                // Skip changes for files outside the current SelectedSubfolders filter
                if (change.Side == SyncSide.Local && !_globFilter.IsIncluded(change.RelativePath, pair))
                    continue;

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Change processing cancelled for '{Path}' (shutdown).", change.RelativePath);
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
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

    /// <returns>true when the cycle reached Drive and finished; false when it failed.</returns>
    private async Task<bool> RunSyncCycleAsync(SyncPair pair, CancellationToken ct)
    {
        SetState(SyncState.Syncing, "Checking for remote changes...");
        _metrics.StartCycle();

        try
        {
            var pageToken = await _stateStore.GetPageTokenAsync(pair.Id);
            if (pageToken is null) return true; // not initialized yet

            IReadOnlyList<Change> changes;
            string newToken;
            try
            {
                (changes, newToken) = await _driveService.ListChangesAsync(pageToken, ct);
            }
            catch (Exception ex) when (DriveErrors.IsExpiredPageToken(ex))
            {
                // The saved change token expired (e.g. the PC was off for a long time). Changes
                // since then are unknown, so take a fresh token and rescan both sides. The rescan
                // matches existing files and never deletes.
                _logger.LogWarning("Drive change token expired for pair {Id}; running a full rescan.", pair.Id);
                var freshToken = await _driveService.GetStartPageTokenAsync(ct);
                await _stateStore.SavePageTokenAsync(pair.Id, freshToken);
                SetState(SyncState.Syncing, "Rescanning...");
                await InitialSyncAsync(pair, ct);
                LogActivity(ActivityType.Info, "", details: "Full rescan after Drive change history expired");
                await _stateStore.SetLastSyncTimeAsync(pair.Id, DateTime.UtcNow);
                _metrics.EndCycle();
                MarkCycleSucceeded(pair);
                return true;
            }
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

                var relativePath = await _driveService.GetRelativePathAsync(change.FileId!, pair.RemoteFolderId, ct);
                if (relativePath is null) continue;
                relativePath = GetRemoteSyncRelativePath(change.File, relativePath);

                // Safety: reject paths that include Google Drive's virtual root name
                // (indicates GetRelativePathAsync walked past the sync root folder)
                if (relativePath.StartsWith("My Drive/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("My Drive\\", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected path '{Path}' for change {FileId} — path resolution walked past sync root.", relativePath, change.FileId);
                    continue;
                }

                if (!_globFilter.IsIncluded(relativePath, pair)) continue;

                await HandleRemoteChangeAsync(change.File, relativePath, pair, ct);
            }

            // Execute any approved deletions
            await _deletionCoordinator.ExecuteApprovedAsync(pair, ct);

            await _stateStore.SetLastSyncTimeAsync(pair.Id, DateTime.UtcNow);
            _metrics.EndCycle();
            MarkCycleSucceeded(pair);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _metrics.EndCycle();
            _logger.LogInformation("Sync cycle cancelled for pair {Id} (shutdown).", pair.Id);
            return false;
        }
        catch (Exception ex)
        {
            _metrics.EndCycle();
            Interlocked.Increment(ref _errors);
            _metrics.RecordError();
            _lastError = ex.Message;
            LogActivity(ActivityType.Error, "", details: $"Sync cycle failed: {ex.Message}");
            var failures = Interlocked.Increment(ref _consecutiveCycleFailures);
            if (NetworkErrors.IsNetworkError(ex))
            {
                _lastError = "Offline - retrying automatically";
                var next = RecoveryBackoff.Next(failures, _config.PollIntervalSeconds, Math.Max(_config.RetryMaxDelaySeconds, 60));
                _logger.LogWarning("Google Drive unreachable for pair {Id} (failure {N}): {Msg}. Next retry in {Sec:0}s or when the network comes back.",
                    pair.Id, failures, ex.Message, next.TotalSeconds);
                SetState(SyncState.Offline);
            }
            else
            {
                _logger.LogError(ex, "Sync cycle failed for pair {Id}", pair.Id);
                SetState(SyncState.Error);
            }
            return false;
        }
    }

    private void MarkCycleSucceeded(SyncPair pair)
    {
        var previous = Interlocked.Exchange(ref _consecutiveCycleFailures, 0);
        if (previous > 0)
        {
            _lastError = null;
            _logger.LogInformation("Connection to Google Drive restored for pair {Id} after {N} failed cycle(s).", pair.Id, previous);
            LogActivity(ActivityType.Info, "", details: "Back online - sync resumed");
        }
        SetState(SyncState.Idle);
    }

    // -------------------------------------------------------------------------
    // Initial sync
    // -------------------------------------------------------------------------

    private async Task InitialSyncAsync(SyncPair pair, CancellationToken ct)
    {
        _logger.LogInformation("Running initial sync for pair '{Id}'...", pair.Id);
        const int maxConcurrency = 8;

        // Collect remote files into a dictionary for O(1) lookup
        var remoteFiles = new Dictionary<string, Google.Apis.Drive.v3.Data.File>(PathKey.Comparer);

        if (pair.SelectedSubfolders.Count > 0)
        {
            // Optimization: only list files from selected subfolders instead of entire Drive root.
            // Find the Drive ID of each selected subfolder, then list files from it.
            var topLevelFolders = await _driveService.ListSubfoldersAsync(pair.RemoteFolderId, "", maxDepth: 1, ct: ct);

            foreach (var selectedFolder in pair.SelectedSubfolders)
            {
                // Match the selected subfolder name to a top-level Drive folder
                var topName = selectedFolder.Contains('/') ? selectedFolder[..selectedFolder.IndexOf('/')] : selectedFolder;
                var match = topLevelFolders.FirstOrDefault(f =>
                    f.Name.Equals(topName, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    _logger.LogWarning("Selected subfolder '{Folder}' not found in Drive root — skipping.", selectedFolder);
                    continue;
                }

                // Add the top-level folder itself (ListFilesWithPathsAsync lists from inside it)
                remoteFiles[match.RelativePath] = new DriveFile
                {
                    Id = match.DriveId, Name = match.Name,
                    MimeType = "application/vnd.google-apps.folder",
                };

                // List all files recursively with paths built during traversal (no extra API calls)
                await foreach (var (file, rel) in _driveService.ListFilesWithPathsAsync(match.DriveId, match.RelativePath, ct))
                {
                    var syncRel = GetRemoteSyncRelativePath(file, rel);
                    if (_globFilter.IsIncluded(syncRel, pair))
                        AddRemote(remoteFiles, syncRel, file);
                }
            }
        }
        else
        {
            // No subfolder selection — list everything from the remote root
            await foreach (var (file, rel) in _driveService.ListFilesWithPathsAsync(pair.RemoteFolderId, "", ct))
            {
                var syncRel = GetRemoteSyncRelativePath(file, rel);
                if (_globFilter.IsIncluded(syncRel, pair))
                    AddRemote(remoteFiles, syncRel, file);
            }
        }

        _logger.LogInformation("Initial sync: found {Remote} remote files for pair '{Id}'.", remoteFiles.Count, pair.Id);

        // Collect local files and categorize them
        var localFiles = new HashSet<string>(PathKey.Comparer);
        var toUpload = new List<(string absPath, string rel)>();
        var toBothExist = new List<(string absPath, string rel, Google.Apis.Drive.v3.Data.File remoteFile)>();

        foreach (var absPath in Directory.EnumerateFiles(pair.LocalPath, "*", SearchOption.AllDirectories))
        {
            var rel = TryMakeRelative(pair.LocalPath, absPath);
            if (rel is null || !_globFilter.IsIncluded(rel, pair)) continue;
            if (new FileInfo(absPath).Length > pair.MaxFileSizeBytes) continue;
            localFiles.Add(rel);

            if (remoteFiles.TryGetValue(rel, out var remoteFile))
                toBothExist.Add((absPath, rel, remoteFile));
            else
                toUpload.Add((absPath, rel));
        }

        // Process local directories — only create on Drive if they don't already exist remotely.
        // Also add the top-level selected subfolders themselves to remoteFiles (they aren't yielded
        // by ListFilesWithPathsAsync since it lists from inside them).
        foreach (var absDir in Directory.EnumerateDirectories(pair.LocalPath, "*", SearchOption.AllDirectories))
        {
            var rel = TryMakeRelative(pair.LocalPath, absDir);
            if (rel is null || !_globFilter.IsIncluded(rel, pair)) continue;

            // Skip directories not under any selected subfolder
            if (pair.SelectedSubfolders.Count > 0 && !pair.SelectedSubfolders.Any(sf =>
                rel.Equals(sf, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(sf + "/", StringComparison.OrdinalIgnoreCase) ||
                sf.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)))
                continue;

            localFiles.Add(rel);

            if (!remoteFiles.ContainsKey(rel))
            {
                await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, rel.Replace('/', Path.DirectorySeparatorChar), ct);
                LogActivity(ActivityType.FolderCreated, rel, details: "Folder synced to Drive");
                _logger.LogInformation("Folder created on Drive: {Path}", rel);
            }
        }

        // Process "both exist" — hash comparison, mostly no transfer (parallel)
        _logger.LogInformation("Initial sync: {Both} files exist on both sides, {Upload} local-only, {Download} remote-only.",
            toBothExist.Count, toUpload.Count, remoteFiles.Count(r => !localFiles.Contains(r.Key)));

        await Parallel.ForEachAsync(toBothExist, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (item, token) => await HandleBothExistAsync(item.rel, item.absPath, item.remoteFile, pair, token));

        // Upload local-only files (parallel, bounded concurrency)
        await Parallel.ForEachAsync(toUpload, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (item, token) => await UploadLocalFileAsync(item.absPath, item.rel, pair, token));

        // Download remote-only files (parallel, bounded concurrency)
        var toDownload = remoteFiles.Where(r => !localFiles.Contains(r.Key)).ToList();
        await Parallel.ForEachAsync(toDownload, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (item, token) => await DownloadRemoteFileAsync(item.Value, item.Key, pair, token));

        // Batch-set all synced files and folders to Synced overlay state
        if (_overlayStore is not null)
        {
            try
            {
                var overlayItems = new List<(string, OverlaySyncState)>();
                foreach (var absDir in Directory.EnumerateDirectories(pair.LocalPath, "*", SearchOption.AllDirectories))
                {
                    var rel = TryMakeRelative(pair.LocalPath, absDir);
                    if (rel is not null && _globFilter.IsIncluded(rel, pair))
                        overlayItems.Add((absDir, OverlaySyncState.Synced));
                }
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

    /// <summary>
    /// Detects local changes that happened while the engine was stopped.
    /// Compares baseline records against actual filesystem state and enqueues
    /// synthetic events for any discrepancies.
    /// </summary>
    private async Task ReconcileLocalChangesAsync(SyncPair pair, CancellationToken ct)
    {
        _logger.LogInformation("Reconciling local changes for pair '{Id}'...", pair.Id);
        int deleted = 0, modified = 0;

        // 1. Check baseline records against actual files — detect deletes and modifications
        var baselinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var rec in _stateStore.StreamRecordsAsync(pair.Id, SyncSide.Local, ct))
        {
            if (rec.IsDeleted) continue;

            // Skip baselines for files outside the current SelectedSubfolders filter
            if (!_globFilter.IsIncluded(rec.RelativePath, pair)) continue;

            baselinePaths.Add(rec.RelativePath);

            var absPath = PathHelper.ToAbsolute(pair.LocalPath, rec.RelativePath);

            if (!IOFile.Exists(absPath) && !Directory.Exists(absPath))
            {
                // Item in baseline but missing locally — was deleted while we were stopped
                _changeQueue.Enqueue(new ChangeEvent
                {
                    SyncPairId   = pair.Id,
                    Side         = SyncSide.Local,
                    Kind         = ChangeKind.Deleted,
                    RelativePath = rec.RelativePath,
                });
                deleted++;
            }
            else
            {
                // File exists — check if modified since baseline
                var detectResult = FastChangeDetector.CheckLocalFile(absPath, rec);
                if (detectResult == ChangeResult.NeedsHash)
                {
                    _changeQueue.Enqueue(new ChangeEvent
                    {
                        SyncPairId   = pair.Id,
                        Side         = SyncSide.Local,
                        Kind         = ChangeKind.Modified,
                        RelativePath = rec.RelativePath,
                    });
                    modified++;
                }
            }
        }

        // 2. Check for new local files not in baseline.
        // Instead of blindly uploading, run through initial-sync logic (check Drive first)
        // to avoid re-uploading files that already exist remotely (e.g. newly added subfolder).
        var newLocalFiles = new List<(string absPath, string rel)>();
        if (Directory.Exists(pair.LocalPath))
        {
            foreach (var absPath in Directory.EnumerateFiles(pair.LocalPath, "*", SearchOption.AllDirectories))
            {
                var rel = TryMakeRelative(pair.LocalPath, absPath);
                if (rel is null || !_globFilter.IsIncluded(rel, pair)) continue;
                if (baselinePaths.Contains(rel)) continue;

                try
                {
                    var fi = new FileInfo(absPath);
                    if (fi.Length > pair.MaxFileSizeBytes) continue;
                }
                catch { continue; }

                newLocalFiles.Add((absPath, rel));
            }
        }

        if (deleted + modified > 0)
            _logger.LogInformation(
                "Reconciliation found {Del} deleted, {Mod} modified for pair '{Id}'.",
                deleted, modified, pair.Id);

        if (newLocalFiles.Count > 0)
        {
            // Run a mini initial sync for these files: list remote counterparts,
            // hash-compare if both exist, only upload truly new files.
            _logger.LogInformation(
                "Reconciliation found {New} new local files for pair '{Id}' — running initial sync for them.",
                newLocalFiles.Count, pair.Id);
            await ReconcileNewFilesViaInitialSyncAsync(pair, newLocalFiles, ct);
        }
        else if (deleted + modified == 0)
        {
            _logger.LogDebug("Reconciliation: no local changes detected for pair '{Id}'.", pair.Id);
        }
    }

    /// <summary>
    /// For files found locally with no baseline, check Drive before uploading.
    /// This handles the case where a new subfolder is added to SelectedSubfolders —
    /// files exist on both sides but have no baselines yet.
    /// </summary>
    private async Task ReconcileNewFilesViaInitialSyncAsync(
        SyncPair pair,
        List<(string absPath, string rel)> newLocalFiles,
        CancellationToken ct)
    {
        const int maxConcurrency = 8;

        // Determine which subfolders contain new files and list remote files for them
        var remoteFiles = new Dictionary<string, DriveFile>(PathKey.Comparer);
        var subfoldersToList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, rel) in newLocalFiles)
        {
            var topFolder = rel.Contains('/') ? rel[..rel.IndexOf('/')] : rel;
            subfoldersToList.Add(topFolder);
        }

        // List remote files for the relevant subfolders (using fast path-building traversal)
        var topLevelFolders = await _driveService.ListSubfoldersAsync(pair.RemoteFolderId, "", maxDepth: 1, ct: ct);
        foreach (var folderName in subfoldersToList)
        {
            var match = topLevelFolders.FirstOrDefault(f =>
                f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;

            // Add the top-level folder itself
            remoteFiles[match.RelativePath] = new DriveFile
            {
                Id = match.DriveId, Name = match.Name,
                MimeType = "application/vnd.google-apps.folder",
            };

            await foreach (var (file, rel) in _driveService.ListFilesWithPathsAsync(match.DriveId, match.RelativePath, ct))
            {
                var syncRel = GetRemoteSyncRelativePath(file, rel);
                if (_globFilter.IsIncluded(syncRel, pair))
                    AddRemote(remoteFiles, syncRel, file);
            }
        }

        _logger.LogInformation("Reconciliation: found {Remote} remote files for comparison.", remoteFiles.Count);

        // Categorize: both-exist vs upload-only
        var toBothExist = new List<(string absPath, string rel, DriveFile remoteFile)>();
        var toUpload = new List<(string absPath, string rel)>();

        foreach (var (absPath, rel) in newLocalFiles)
        {
            if (remoteFiles.TryGetValue(rel, out var remoteFile))
                toBothExist.Add((absPath, rel, remoteFile));
            else
                toUpload.Add((absPath, rel));
        }

        _logger.LogInformation("Reconciliation: {Both} files exist on both sides, {Upload} local-only.",
            toBothExist.Count, toUpload.Count);

        // Process both-exist (hash compare, parallel)
        await Parallel.ForEachAsync(toBothExist,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (item, token) => await HandleBothExistAsync(item.rel, item.absPath, item.remoteFile, pair, token));

        // Upload truly new files (parallel)
        await Parallel.ForEachAsync(toUpload,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (item, token) => await UploadLocalFileAsync(item.absPath, item.rel, pair, token));

        // Download remote-only files (files on Drive but not locally)
        var localRels = new HashSet<string>(newLocalFiles.Select(f => f.rel), PathKey.Comparer);
        var toDownload = remoteFiles.Where(r => !localRels.Contains(r.Key)).ToList();

        // Only download files that also have no baseline (truly new remote files)
        var toDownloadNew = new List<KeyValuePair<string, DriveFile>>();
        foreach (var item in toDownload)
        {
            var existing = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, item.Key);
            if (existing is null)
                toDownloadNew.Add(item);
        }

        if (toDownloadNew.Count > 0)
        {
            _logger.LogInformation("Reconciliation: {Download} remote-only files to download.", toDownloadNew.Count);
            await Parallel.ForEachAsync(toDownloadNew,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                async (item, token) => await DownloadRemoteFileAsync(item.Value, item.Key, pair, token));
        }
    }

    // -------------------------------------------------------------------------
    // Local change handlers
    // -------------------------------------------------------------------------

    private async Task HandleLocalChangeAsync(ChangeEvent ev, SyncPair pair, CancellationToken ct)
    {
        var absolutePath = PathHelper.ToAbsolute(pair.LocalPath, ev.RelativePath);

        // Directory events: create the folder on Drive, log, and return
        if (Directory.Exists(absolutePath) && ev.Kind != ChangeKind.Deleted)
        {
            if (ev.Kind == ChangeKind.Created)
            {
                var parentRel = Path.GetDirectoryName(ev.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
                var parentId = parentRel.Length > 0
                    ? await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, parentRel, ct)
                    : pair.RemoteFolderId;
                await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, ev.RelativePath.Replace('/', Path.DirectorySeparatorChar), ct);
                await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Synced, ct);
                LogActivity(ActivityType.FolderCreated, ev.RelativePath);
                _logger.LogInformation("Folder created: {Path}", ev.RelativePath);
            }
            else if (ev.Kind == ChangeKind.Renamed && ev.OldRelativePath is not null)
            {
                // Folder rename: create new folder path on Drive
                await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, ev.RelativePath.Replace('/', Path.DirectorySeparatorChar), ct);
                await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Synced, ct);
                LogActivity(ActivityType.Renamed, ev.RelativePath, details: $"Folder renamed from {ev.OldRelativePath}");
                _logger.LogInformation("Folder renamed: '{Old}' → '{New}'", ev.OldRelativePath, ev.RelativePath);

                // FSW does not fire events for files inside a renamed folder on Windows.
                // Enqueue each file at the new path as Created so it gets uploaded under
                // the new Drive folder. Old-path baselines will be detected as missing
                // locally on the next reconciliation and cleaned up via the deletion pipeline.
                foreach (var absFile in Directory.EnumerateFiles(absolutePath, "*", SearchOption.AllDirectories))
                {
                    var rel = TryMakeRelative(pair.LocalPath, absFile);
                    if (rel is not null && _globFilter.IsIncluded(rel, pair))
                        _changeQueue.Enqueue(new ChangeEvent
                        {
                            SyncPairId   = pair.Id,
                            Side         = SyncSide.Local,
                            Kind         = ChangeKind.Created,
                            RelativePath = rel,
                        });
                }
            }
            return;
        }

        // If file was deleted while we were waiting, treat as a delete
        if (!IOFile.Exists(absolutePath) && !Directory.Exists(absolutePath) && ev.Kind != ChangeKind.Deleted)
        {
            _logger.LogDebug("File vanished before processing: {Path} — treating as delete.", ev.RelativePath);
            await HandleLocalDeleteAsync(ev with { Kind = ChangeKind.Deleted }, pair, ct);
            return;
        }

        // Wait until the file is readable and has stopped changing (apps write in bursts;
        // uploading mid-save sends a half-written file).
        if (IOFile.Exists(absolutePath) && !FileLockHelper.IsSettled(absolutePath, SettleTime) && ev.RetryCount < FileLockHelper.MaxRetries)
        {
            _logger.LogDebug("File busy or still changing: {Path} — requeueing (attempt {N})", ev.RelativePath, ev.RetryCount + 1);
            await Task.Delay(FileLockHelper.RetryDelay(ev.RetryCount), ct);
            _changeQueue.Enqueue(ev with { RetryCount = ev.RetryCount + 1 });
            return;
        }

        var baseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.RelativePath);

        // Exported Google Docs/Sheets/Slides are copies of a Google file: never upload them back
        // (that would create a duplicate next to the real Google file). Edit them in Google instead.
        if (ExportedFiles.IsExportOf(baseline) && ev.Kind != ChangeKind.Renamed)
        {
            _logger.LogInformation("Not uploading '{Path}': it is an exported copy of a Google file (edit it in Google Drive).", ev.RelativePath);
            return;
        }

        if (ev.Kind == ChangeKind.Renamed && ev.OldRelativePath is not null
            && await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath) is { DriveFileId: not null } movedBaseline)
        {
            // Rename/move the Drive file in place: no re-upload, keeps its ID, history and sharing.
            // Also covers case-only renames ("report.pdf" -> "Report.pdf").
            var newName   = Path.GetFileName(ev.RelativePath);
            var newParent = Path.GetDirectoryName(ev.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            var newParentId = newParent.Length > 0
                ? await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, newParent, ct)
                : pair.RemoteFolderId;
            var moved = await _driveService.MoveFileAsync(movedBaseline.DriveFileId, newName, newParentId, ct);

            // Re-key baselines: remove the old path first (matters for case-only renames).
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath);
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Local, ev.OldRelativePath);
            await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, ev.RelativePath, moved));
            var movedLocal = await BuildLocalRecordAsync(pair.Id, ev.RelativePath, absolutePath);
            if (movedLocal is not null)
            {
                if (!string.Equals(movedLocal.ContentHash, moved.Md5Checksum ?? movedBaseline.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Content also changed while renaming (e.g. save-as over): update in place.
                    await UploadLocalFileAsync(absolutePath, ev.RelativePath, pair, ct, moved.Id);
                }
                else
                {
                    await _stateStore.UpsertRecordAsync(movedLocal);
                }
            }
            await SetOverlayAsync(ev.RelativePath, OverlaySyncState.Synced, ct);
            LogActivity(ActivityType.Renamed, ev.RelativePath, details: $"Renamed from {ev.OldRelativePath}");
            _logger.LogInformation("Renamed on Drive: '{Old}' → '{New}'", ev.OldRelativePath, ev.RelativePath);
        }
        else if (ev.Kind == ChangeKind.Renamed && ev.OldRelativePath is not null)
        {
            // Not on Drive yet under the old name: upload the new path (create before delete)
            var oldBaseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath);
            await UploadLocalFileAsync(absolutePath, ev.RelativePath, pair, ct); // updates both baselines

            // Only trash old Drive file after upload succeeds — prevents data loss on failure
            if (oldBaseline?.DriveFileId is not null)
                await _driveService.TrashFileAsync(oldBaseline.DriveFileId, ct);

            // Clean up old path baselines
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Remote, ev.OldRelativePath);
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Local, ev.OldRelativePath);
            LogActivity(ActivityType.Renamed, ev.RelativePath, details: $"Renamed from {ev.OldRelativePath}");
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
                Interlocked.Increment(ref _filesUploaded);
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
        if (!SyncModePolicy.PropagatesLocalDeletes(pair))
        {
            _logger.LogDebug("Backup mode: local delete of '{Path}' is kept on Drive.", ev.RelativePath);
            return;
        }
        var baseline = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, ev.RelativePath);
        if (baseline is null)
        {
            await TrySubmitFolderDeleteAsync(ev.RelativePath, pair, ct);
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

    /// <summary>
    /// A local folder was deleted: trash the matching Drive folder too - but only when every file
    /// inside it on Drive is one SyncIt knows (has a baseline), so nothing that never reached this PC
    /// is thrown away. It goes through the deletion coordinator (grace period + mass-delete protection).
    /// </summary>
    private async Task TrySubmitFolderDeleteAsync(string relativePath, SyncPair pair, CancellationToken ct)
    {
        var abs = PathHelper.ToAbsolute(pair.LocalPath, relativePath);
        if (Directory.Exists(abs) || IOFile.Exists(abs) || !Directory.Exists(pair.LocalPath))
        {
            _logger.LogDebug("Local delete of '{Path}' has no remote baseline - nothing to propagate.", relativePath);
            return;
        }

        var folderId = pair.RemoteFolderId;
        DriveFileLite? folder = null;
        foreach (var seg in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = await _driveService.FindFileAsync(folderId, seg, ct);
            if (f?.Id is null || f.MimeType != "application/vnd.google-apps.folder") { folder = null; break; }
            folder = new DriveFileLite(f.Id);
            folderId = f.Id;
        }
        if (folder is null)
        {
            _logger.LogDebug("Local delete of '{Path}': no matching Drive folder.", relativePath);
            return;
        }

        var prefix = relativePath.Replace('\\', '/').TrimEnd('/') + "/";
        await foreach (var (file, rel) in _driveService.ListFilesWithPathsAsync(folder.Id, "", ct))
        {
            if (file.MimeType == "application/vnd.google-apps.folder") continue;
            if (await _stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, prefix + rel) is null &&
                await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, prefix + rel) is null)
            {
                _logger.LogInformation("Not trashing Drive folder '{Path}': it contains '{File}' that was never synced to this PC.", relativePath, rel);
                return;
            }
        }

        await _deletionCoordinator.SubmitAsync(pair, new[]
        {
            new DeletionRecord
            {
                SyncPairId    = pair.Id,
                RelativePath  = relativePath.Replace('\\', '/'),
                Side          = SyncSide.Remote,
                DriveFileId   = folder.Id,
                TriggerReason = "FolderDeletedLocally",
            },
        }, ct);
        LogActivity(ActivityType.FolderDeleted, relativePath, details: "Folder deleted on this PC - Drive folder queued for trash");
    }

    private sealed record DriveFileLite(string Id);

    // -------------------------------------------------------------------------
    // Remote change handlers
    // -------------------------------------------------------------------------

    private async Task HandleRemoteChangeAsync(
        Google.Apis.Drive.v3.Data.File file,
        string relativePath,
        SyncPair pair,
        CancellationToken ct)
    {
        if (!SyncModePolicy.AllowsDownload(pair)) return; // backup only: Drive changes never touch the PC
        var localAbsPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);

        // Case-only rename on Drive ("notes.txt" -> "Notes.txt"): rename the local file too.
        // Windows treats both names as the same file, so the normal rename path missed it.
        if (file.Id is not null &&
            await _stateStore.GetRecordByDriveFileIdAsync(pair.Id, SyncSide.Remote, file.Id) is { } byId &&
            PathKey.IsCaseOnlyChange(byId.RelativePath, relativePath))
        {
            var oldAbs = PathHelper.ToAbsolute(pair.LocalPath, byId.RelativePath);
            if (IOFile.Exists(oldAbs))
            {
                var temp = oldAbs + ".syncit-tmp-rename";
                _atomicWriter.SuppressPath(oldAbs);
                _atomicWriter.SuppressPath(localAbsPath);
                IOFile.Move(oldAbs, temp);
                IOFile.Move(temp, localAbsPath);
            }
            var oldLocal = await _stateStore.GetRecordAsync(pair.Id, SyncSide.Local, byId.RelativePath);
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Remote, byId.RelativePath);
            await _stateStore.RemoveRecordAsync(pair.Id, SyncSide.Local, byId.RelativePath);
            byId.RelativePath = relativePath;
            await _stateStore.UpsertRecordAsync(byId);
            if (oldLocal is not null)
            {
                oldLocal.RelativePath = relativePath;
                await _stateStore.UpsertRecordAsync(oldLocal);
            }
            LogActivity(ActivityType.Renamed, relativePath, details: $"Renamed from {byId.RelativePath}");
            _logger.LogInformation("Remote case-only rename: '{Old}' → '{New}'", byId.RelativePath, relativePath);
        }

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
            var oldRemote = await _stateStore.GetRecordByDriveFileIdAsync(pair.Id, SyncSide.Remote, file.Id);

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
                LogActivity(ActivityType.Renamed, relativePath, details: $"Renamed from {oldRemote.RelativePath}");
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
        if (!SyncModePolicy.AllowsLocalDeleteFromRemote(pair)) return; // backup only: never delete on the PC
        // Find by driveFileId since we only have the ID, not the path
        var found = await _stateStore.GetRecordByDriveFileIdAsync(pair.Id, SyncSide.Remote, driveFileId);
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

    private async Task UploadLocalFileAsync(string absolutePath, string relativePath, SyncPair pair, CancellationToken ct,
        string? existingDriveFileId = null)
    {
        if (!IOFile.Exists(absolutePath)) return;

        var fileName   = Path.GetFileName(relativePath);
        var parentRel  = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var parentId   = parentRel.Length > 0
            ? await _driveService.EnsureFolderPathAsync(pair.RemoteFolderId, parentRel, ct)
            : pair.RemoteFolderId;

        // Look before creating: if Drive already has a file with this name here (no baseline yet,
        // e.g. after a reset, a crash mid-upload, or another device), reuse it instead of
        // creating a duplicate. Same content: just record it. Different content: conflict rules.
        if (existingDriveFileId is null)
        {
            var existingRemote = await _driveService.FindFileAsync(parentId, fileName, ct);
            if (existingRemote?.Id is not null)
            {
                var localHash = await HashHelper.ComputeMd5Async(absolutePath, ct);
                var info = new FileInfo(absolutePath);
                var localRecord = new FileRecord
                {
                    SyncPairId = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
                    SizeBytes = info.Length, ModifiedUtc = info.LastWriteTimeUtc, ContentHash = localHash,
                };
                if (string.Equals(localHash, existingRemote.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    await _stateStore.UpsertRecordAsync(localRecord);
                    await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, existingRemote));
                    await SetOverlayAsync(relativePath, OverlaySyncState.Synced, ct);
                    _logger.LogInformation("Already on Drive (same content), not uploading again: {Path}", relativePath);
                    return;
                }
                _logger.LogInformation("Drive already has '{Path}' with different content; applying conflict rules instead of creating a copy.", relativePath);
                await ResolveConflictAsync(localRecord, existingRemote, relativePath, pair, ct);
                return;
            }
        }

        await _transferSlim.WaitAsync(ct);
        try
        {
            var fileSize = new FileInfo(absolutePath).Length;
            var progress = TransferTracker.Start(pair.Id, relativePath, TransferDirection.Upload, fileSize);
            await SetOverlayAsync(relativePath, OverlaySyncState.Syncing, ct);

            var mimeType   = MimeTypeFor(relativePath);
            // When the file already exists on Drive (e.g. a conflict found during the first sync),
            // update it in place. Creating a new file would leave two files with the same name on Drive.
            var uploaded   = existingDriveFileId is not null
                ? await _driveService.UpdateFileAsync(existingDriveFileId, absolutePath, mimeType, progress, ct)
                : await _driveService.UploadFileAsync(absolutePath, fileName, parentId, mimeType, progress, ct);
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
            Interlocked.Increment(ref _filesUploaded);
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
        if (!SyncModePolicy.AllowsDownload(pair))
        {
            _logger.LogDebug("Backup mode: not downloading '{Path}'.", relativePath);
            return;
        }
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
            await SetOverlayAsync(relativePath, OverlaySyncState.Synced, ct);
            LogActivity(ActivityType.FolderCreated, relativePath, details: "Remote folder synced locally");
            return;
        }

        var localPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);

        // Determine output path (Google Docs get a different extension)
        string outputPath = localPath;
        string? exportMime = null;

        if (GoogleDocExportMap.TryGetExport(file.MimeType, pair, out exportMime, out var ext))
        {
            if (!pair.ExportGoogleDocs)
            {
                _logger.LogDebug("Skipping Google Doc (export disabled): {Path}", relativePath);
                return;
            }
            outputPath = localPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? localPath
                : localPath + ext;
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
                ExportedFiles.ClearReadOnly(outputPath); // a read-only target would block the replace
                await _atomicWriter.WriteAsync(ms, outputPath, null, file.ModifiedTimeDateTimeOffset?.UtcDateTime, ct);
                if (pair.ExportReadOnly) ExportedFiles.MarkReadOnly(outputPath);
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
            Interlocked.Increment(ref _filesDownloaded);
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
        // Google Apps files (Docs, Sheets, etc.) have no md5Checksum — can't hash-compare.
        // If both sides exist, just record baselines and skip transfer.
        if (remoteFile.Md5Checksum is null)
        {
            var localInfo = new FileInfo(localAbsPath);
            var localHash = await HashHelper.ComputeMd5Async(localAbsPath, ct);
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
                SizeBytes  = localInfo.Length, ModifiedUtc = localInfo.LastWriteTimeUtc, ContentHash = localHash,
            });
            await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, remoteFile));
            return;
        }

        var hash = await HashHelper.ComputeMd5Async(localAbsPath, ct);
        if (hash.Equals(remoteFile.Md5Checksum, StringComparison.OrdinalIgnoreCase))
        {
            // Content identical — just record baseline, no transfer needed
            var localInfo = new FileInfo(localAbsPath);
            await _stateStore.UpsertRecordAsync(new FileRecord
            {
                SyncPairId = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
                SizeBytes  = localInfo.Length, ModifiedUtc = localInfo.LastWriteTimeUtc, ContentHash = hash,
            });
            await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, remoteFile));
            return;
        }

        // Content differs on initial sync — treat as conflict with no baseline
        var conflictInfo = new FileInfo(localAbsPath);
        var localRecord = new FileRecord
        {
            SyncPairId   = pair.Id, Side = SyncSide.Local, RelativePath = relativePath,
            ContentHash  = hash, ModifiedUtc = conflictInfo.LastWriteTimeUtc,
            SizeBytes    = conflictInfo.Length,
        };
        await ResolveConflictAsync(localRecord, remoteFile, relativePath, pair, ct);
    }

    /// <summary>Drive ID to update in place, or null for Google Docs (which can't take binary content).</summary>
    private static string? ExistingId(Google.Apis.Drive.v3.Data.File remoteFile) =>
        string.IsNullOrEmpty(remoteFile.Id) || (remoteFile.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) ?? false)
            ? null : remoteFile.Id;

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

        if (SyncModePolicy.ForceLocalWins(pair) && action.Outcome is ConflictOutcome.UseRemote or ConflictOutcome.KeepBoth)
            action = new ConflictResolutionAction { Outcome = ConflictOutcome.UseLocal, ReasonDescription = "Backup mode: the PC copy wins" };

        _logger.LogInformation("Conflict on '{Path}': {Outcome}", relativePath, action.Outcome);

        var localAbsPath = PathHelper.ToAbsolute(pair.LocalPath, relativePath);

        switch (action.Outcome)
        {
            case ConflictOutcome.UseLocal:
                await UploadLocalFileAsync(localAbsPath, relativePath, pair, ct, ExistingId(remoteFile));
                break;

            case ConflictOutcome.UseRemote:
                await DownloadRemoteFileAsync(remoteFile, relativePath, pair, ct);
                break;

            case ConflictOutcome.KeepBoth:
                // Keep local as-is; download remote to conflict path
                if (action.KeepBothRemotePath is not null)
                    await DownloadRemoteFileAsync(remoteFile, action.KeepBothRemotePath, pair, ct);
                // Also upload local to ensure Drive has the local version (in place, no duplicate)
                await UploadLocalFileAsync(localAbsPath, relativePath, pair, ct, ExistingId(remoteFile));
                break;

            case ConflictOutcome.NoConflict:
                // Update both baselines so future change detection works correctly
                await _stateStore.UpsertRecordAsync(BuildRemoteRecord(pair.Id, relativePath, remoteFile));
                await _stateStore.UpsertRecordAsync(localRecord);
                break;
        }

        Interlocked.Increment(ref _conflictsResolved);
        LogActivity(ActivityType.ConflictResolved, relativePath, details: action.ReasonDescription);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void LogActivity(ActivityType type, string relativePath, long sizeBytes = 0, string? details = null)
    {
        if (_activityLog is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _activityLog.RecordAsync(new ActivityRecord
                {
                    SyncPairId = _pair.Id,
                    Type = type,
                    RelativePath = relativePath,
                    SizeBytes = sizeBytes,
                    Details = details,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to record activity log for {Type} '{Path}'", type, relativePath);
            }
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _duplicateRemotePaths = new(PathKey.Comparer);

    /// <summary>Paths that have two or more files with the same name in the same Drive folder.</summary>
    public IReadOnlyCollection<string> DuplicateRemotePaths => _duplicateRemotePaths.Keys.ToList();

    /// <summary>
    /// Adds a listed Drive item. Drive allows several files with the same name in one folder;
    /// a Windows folder can't hold them. SyncIt keeps the most recently modified one and warns,
    /// instead of silently picking one at random.
    /// </summary>
    private void AddRemote(Dictionary<string, DriveFile> remoteFiles, string relativePath, DriveFile file)
    {
        if (remoteFiles.TryGetValue(relativePath, out var existing) && existing.Id != file.Id)
        {
            if (_duplicateRemotePaths.TryAdd(relativePath, 0))
            {
                _logger.LogWarning(
                    "Google Drive has more than one item named '{Path}'. SyncIt syncs the most recently modified one; " +
                    "rename or remove the extra copy on drive.google.com.", relativePath);
                LogActivity(ActivityType.Error, relativePath, details: "Duplicate name on Google Drive: more than one file with this name. Syncing the newest.");
            }
            var newer = (file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue) > (existing.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue);
            if (!newer) return;
        }
        remoteFiles[relativePath] = file;
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

    private string GetRemoteSyncRelativePath(Google.Apis.Drive.v3.Data.File file, string relativePath)
    {
        if (GoogleDocExportMap.TryGetExport(file.MimeType, _pair, out _, out var ext))
            return (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : relativePath + ext).Replace('\\', '/');

        return relativePath;
    }

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
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException) { _logger.LogWarning("Engine tasks did not complete within 30s timeout."); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error waiting for engine tasks to stop."); }
            }

            _internalCts.Dispose();
        }

        StopFileSystemWatcher();
        _transferSlim.Dispose();
        if (_changeQueue is IDisposable d) d.Dispose();
    }

    private void StopFileSystemWatcher()
    {
        if (_fsw is null) return;

        _fsw.EnableRaisingEvents = false;
        if (_fswCreated is not null) _fsw.Created -= _fswCreated;
        if (_fswChanged is not null) _fsw.Changed -= _fswChanged;
        if (_fswDeleted is not null) _fsw.Deleted -= _fswDeleted;
        if (_fswRenamed is not null) _fsw.Renamed -= _fswRenamed;
        if (_fswError is not null)   _fsw.Error   -= _fswError;
        _fsw.Dispose();
        _fsw = null;
    }
}
