namespace SyncIt.Core.Models;

public enum ActivityType
{
    Upload,
    Download,
    ConflictResolved,
    Deleted,
    Error,
    Renamed,
    FolderCreated,
    FolderDeleted,
    Info,
}

/// <summary>
/// A single sync activity event — persisted to activity.db for history tracking.
/// </summary>
public class ActivityRecord
{
    public long Id { get; set; }
    public string SyncPairId { get; set; } = string.Empty;
    public ActivityType Type { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Additional context: error message, conflict resolution reason, etc.</summary>
    public string? Details { get; set; }
}
