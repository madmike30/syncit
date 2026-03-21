using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;

namespace SyncIt.Core.Services;

/// <summary>
/// Orchestrates all file deletions through the full safety pipeline.
/// This is the single gateway — nothing gets deleted without going through here.
/// </summary>
public sealed class DeletionCoordinator : IDeletionCoordinator
{
    private readonly SyncConfig _config;
    private readonly DeletionSafetyValidator _validator;
    private readonly DeletionJournal _journal;
    private readonly LocalTrashManager _trashManager;
    private readonly IGoogleDriveService _driveService;
    private readonly IFileStateStore _stateStore;
    private readonly AtomicFileWriter _atomicWriter;
    private readonly ILogger<DeletionCoordinator> _logger;

    // Raised when deletions are frozen — SyncWorker forwards this to IPC
    public event Action<string, IReadOnlyList<DeletionRecord>>? DeletionFreezeTriggered;

    public DeletionCoordinator(
        SyncConfig config,
        DeletionSafetyValidator validator,
        DeletionJournal journal,
        LocalTrashManager trashManager,
        IGoogleDriveService driveService,
        IFileStateStore stateStore,
        AtomicFileWriter atomicWriter,
        ILogger<DeletionCoordinator> logger)
    {
        _config       = config;
        _validator    = validator;
        _journal      = journal;
        _trashManager = trashManager;
        _driveService = driveService;
        _stateStore   = stateStore;
        _atomicWriter = atomicWriter;
        _logger       = logger;
    }

    public async Task<bool> SubmitAsync(
        SyncPair pair,
        IReadOnlyList<DeletionRecord> pendingDeletions,
        CancellationToken ct = default)
    {
        if (pendingDeletions.Count == 0) return false;

        var validation = await _validator.ValidateAsync(pair, pendingDeletions, ct);

        if (validation.ShouldFreeze)
        {
            // Retroactively freeze ALL existing Pending deletions for this pair
            var retroFrozen = await _journal.FreezeAllPendingAsync(pair.Id, ct);
            if (retroFrozen > 0)
                _logger.LogWarning("Retroactively froze {Count} existing pending deletions for pair {PairId}.", retroFrozen, pair.Id);

            // Mark new deletions as Frozen in journal
            foreach (var del in validation.ApprovedDeletions)
            {
                var frozen = del with { Status = DeletionStatus.Frozen };
                await _journal.RecordAsync(frozen, ct);
            }

            DeletionFreezeTriggered?.Invoke(pair.Id, validation.ApprovedDeletions);
            _logger.LogWarning("Deletion freeze for pair {PairId}: {Count} new files frozen.", pair.Id, validation.ApprovedDeletions.Count);
            return true;
        }

        // Record with grace period
        foreach (var del in validation.ApprovedDeletions)
        {
            var withGrace = del with
            {
                Status               = DeletionStatus.Pending,
                GraceCyclesRemaining = _config.DeletionGraceCycles,
            };
            await _journal.RecordAsync(withGrace, ct);
            _logger.LogInformation(
                "Deletion queued: '{Path}' (side={Side}, reason={Reason}) — will execute in {Cycles} poll cycle(s).",
                del.RelativePath, del.Side, del.TriggerReason, _config.DeletionGraceCycles);
        }

        return false;
    }

    public async Task ExecuteApprovedAsync(SyncPair pair, CancellationToken ct = default)
    {
        // Tick grace cycles for all pending deletions — items reaching 0 execute below
        await _journal.DecrementAllPendingGraceCyclesAsync(pair.Id, ct);

        // Buffer ready items into a list first — this closes the SQLite reader before
        // we start writing, avoiding "table is locked" errors from Cache=Shared connections.
        var ready = new List<DeletionRecord>();
        await foreach (var del in _journal.GetReadyToExecuteAsync(pair.Id, ct))
            ready.Add(del);

        foreach (var del in ready)
        {
            var record = del; // local var so ExecuteSingleDeletionAsync can update TrashPath
            try
            {
                record = await ExecuteSingleDeletionAsync(record, pair, ct);
                await _journal.UpdateStatusAsync(record.Id, DeletionStatus.Executed, DateTime.UtcNow, record.TrashPath, ct);
                await _stateStore.MarkDeletedAsync(record.SyncPairId, record.Side, record.RelativePath);

                _logger.LogInformation(
                    "Executed deletion: '{Path}' (side={Side}, trigger={Reason})",
                    record.RelativePath, record.Side, record.TriggerReason);
            }
            catch (Exception ex)
            {
                // grace_cycles goes more negative each cycle (-1, -2, -3...).
                // After 3 failed attempts, give up and cancel the deletion.
                if (del.GraceCyclesRemaining <= -3)
                {
                    _logger.LogError(ex,
                        "Deletion of '{Path}' failed 3 times — cancelling permanently.",
                        record.RelativePath);
                    await _journal.UpdateStatusAsync(record.Id, DeletionStatus.Cancelled, ct: ct);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Failed to execute deletion of '{Path}' (attempt {N}/3). Will retry next cycle.",
                        record.RelativePath, Math.Abs(del.GraceCyclesRemaining));
                    // Leave as Pending — DecrementAllPendingGraceCyclesAsync will tick it down further
                }
            }
        }
    }

    public async Task ApproveFrozenAsync(string syncPairId, CancellationToken ct = default)
    {
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync(syncPairId, ct))
            frozen.Add(del);

        foreach (var del in frozen)
        {
            // Unfreeze with 0 grace cycles so they execute on the next cycle
            await _journal.UpdateStatusAsync(del.Id, DeletionStatus.Pending, ct: ct);
            _logger.LogInformation("User approved frozen deletion: '{Path}'", del.RelativePath);
        }
    }

    public async Task RenotifyFrozenAsync(string syncPairId, CancellationToken ct = default)
    {
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync(syncPairId, ct))
            frozen.Add(del);

        if (frozen.Count > 0)
        {
            _logger.LogWarning("Re-notifying about {Count} frozen deletions from previous session for pair {PairId}.", frozen.Count, syncPairId);
            DeletionFreezeTriggered?.Invoke(syncPairId, frozen);
        }
    }

    public async Task CancelFrozenAsync(string syncPairId, CancellationToken ct = default)
    {
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync(syncPairId, ct))
            frozen.Add(del);

        foreach (var del in frozen)
        {
            await _journal.UpdateStatusAsync(del.Id, DeletionStatus.Cancelled, ct: ct);
            _logger.LogInformation("User cancelled frozen deletion: '{Path}'", del.RelativePath);
        }
    }

    // -------------------------------------------------------------------------
    // Private execution
    // -------------------------------------------------------------------------

    private async Task<DeletionRecord> ExecuteSingleDeletionAsync(DeletionRecord del, SyncPair pair, CancellationToken ct)
    {
        if (del.Side == SyncSide.Remote)
        {
            // Propagate local delete to Drive → Trash (NOT permanent delete)
            if (del.DriveFileId is not null)
            {
                await _driveService.TrashFileAsync(del.DriveFileId, ct);
                _logger.LogInformation("Trashed Drive file {FileId} ('{Path}')", del.DriveFileId, del.RelativePath);
            }
            else
            {
                // No DriveFileId means we can't find the remote file to trash.
                // This can happen if the file was never fully synced. Skip gracefully
                // but log as error so it's visible.
                _logger.LogError(
                    "Cannot trash Drive file for '{Path}': no DriveFileId in deletion record. " +
                    "The remote file may not have been synced. Marking as executed to clear the queue.",
                    del.RelativePath);
            }
        }
        else
        {
            // Remote file was deleted → move local copy to .syncit-trash
            var localPath = PathHelper.ToAbsolute(pair.LocalPath, del.RelativePath);
            if (File.Exists(localPath))
            {
                // Suppress FSW event for the file disappearing — we're moving it, not the user
                _atomicWriter.SuppressPath(localPath);
                var trashPath = _trashManager.MoveToTrash(pair.Id, localPath, del.RelativePath);
                del = del with { TrashPath = trashPath };
            }
            else
            {
                _logger.LogDebug("Local file '{Path}' already gone — no trash move needed.", localPath);
            }
        }

        return del;
    }

}
