using SyncIt.Core.Models;
using SyncIt.Core.Safety;

namespace SyncIt.Core.Services;

/// <summary>
/// Coordinates all deletion operations through the three-layer safety system:
///   Layer 1 — Baseline validity (only delete files with known history)
///   Layer 2 — Threshold freeze (too many at once → require approval)
///   Layer 3 — Grace period + soft delete (Drive Trash / local .syncit-trash)
/// </summary>
public interface IDeletionCoordinator
{
    /// <summary>
    /// Submit a batch of pending deletions for a sync cycle.
    /// Returns whether deletions were frozen (true = user must approve before execution).
    /// </summary>
    Task<bool> SubmitAsync(SyncPair pair, IReadOnlyList<DeletionRecord> pendingDeletions, CancellationToken ct = default);

    /// <summary>
    /// Execute deletions that have passed the grace period.
    /// Called at the end of each sync cycle.
    /// </summary>
    Task ExecuteApprovedAsync(SyncPair pair, CancellationToken ct = default);

    /// <summary>
    /// User approved frozen deletions (from tray app approval dialog).
    /// Unfreezes and sets to Pending with 0 grace cycles (execute immediately next cycle).
    /// </summary>
    Task ApproveFrozenAsync(string syncPairId, CancellationToken ct = default);

    /// <summary>
    /// User rejected frozen deletions.
    /// Marks them as Cancelled — they are never executed.
    /// </summary>
    Task CancelFrozenAsync(string syncPairId, CancellationToken ct = default);

    /// <summary>
    /// Re-raise freeze notification for any frozen deletions from a previous session.
    /// Called on startup so the user sees the approval dialog again.
    /// </summary>
    Task RenotifyFrozenAsync(string syncPairId, CancellationToken ct = default);
}
