namespace SyncIt.Core.Models;

public enum DeletionStatus
{
    Pending   = 0,  // queued, within grace period
    Executed  = 1,  // successfully soft-deleted
    Frozen    = 2,  // threshold exceeded — awaiting user approval
    Cancelled = 3,  // user rejected the deletion
}

/// <summary>
/// Append-only audit record written to delete-journal.db before any deletion is executed.
/// This is the complete history of everything SyncIt has ever deleted or considered deleting.
/// </summary>
public record class DeletionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string    SyncPairId    { get; set; } = string.Empty;
    public string    RelativePath  { get; set; } = string.Empty;
    public SyncSide  Side          { get; set; }
    public string?   DriveFileId   { get; set; }
    public long      SizeBytes     { get; set; }
    public DateTime  ModifiedUtc   { get; set; }

    /// <summary>UTC time when the deletion was first detected and queued.</summary>
    public DateTime  ScheduledUtc  { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time the deletion was actually executed. Null until then.</summary>
    public DateTime? ExecutedUtc   { get; set; }

    /// <summary>"DeletedLocally" | "DeletedRemotely"</summary>
    public string    TriggerReason { get; set; } = string.Empty;

    public DeletionStatus Status   { get; set; } = DeletionStatus.Pending;

    /// <summary>
    /// For remote deletions: the path in .syncit-trash where the local file was moved.
    /// For local deletions: the Drive trash ID (empty — Drive handles its own trash).
    /// </summary>
    public string? TrashPath { get; set; }

    /// <summary>Which grace cycle this is in (counts down from DeletionGraceCycles to 0).</summary>
    public int GraceCyclesRemaining { get; set; }
}
