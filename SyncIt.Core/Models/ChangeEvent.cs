namespace SyncIt.Core.Models;

public enum ChangeKind
{
    Created,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>
/// A detected change on either the local filesystem or Google Drive.
/// Produced by FileSystemWatcher (local) or the Drive polling loop (remote).
/// </summary>
public record class ChangeEvent
{
    public string    SyncPairId    { get; set; } = string.Empty;
    public SyncSide  Side          { get; set; }
    public ChangeKind Kind         { get; set; }
    public string    RelativePath  { get; set; } = string.Empty;

    /// <summary>Previous path — only populated for Renamed events.</summary>
    public string?   OldRelativePath { get; set; }

    public DateTime  DetectedUtc   { get; set; } = DateTime.UtcNow;

    /// <summary>Populated for remote events from the Drive Changes API.</summary>
    public string?   DriveFileId   { get; set; }

    /// <summary>How many times delivery of this event has been retried (e.g. file locked).</summary>
    public int RetryCount { get; set; }
}
