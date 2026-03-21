namespace SyncIt.Core.Models;

public enum SyncState
{
    Idle,
    Syncing,
    Paused,
    Error,
    Authenticating,
    SafetyHalt,        // startup validation failed — awaiting user action
    DeletionFrozen,    // deletion threshold exceeded — awaiting user approval
}

/// <summary>Runtime status snapshot — sent over IPC to the tray app every 10s and on changes.</summary>
public class SyncStatus
{
    public SyncState State                 { get; set; } = SyncState.Idle;
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public int FilesUploadedSession        { get; set; }
    public int FilesDownloadedSession      { get; set; }
    public int ConflictsResolvedSession    { get; set; }
    public int ErrorsSession               { get; set; }

    /// <summary>Short description of what the service is doing right now.</summary>
    public string? CurrentActivity { get; set; }

    /// <summary>Last error message (if any).</summary>
    public string? LastError       { get; set; }

    /// <summary>Google account email (fetched periodically).</summary>
    public string? AccountEmail    { get; set; }

    /// <summary>Drive storage used in bytes.</summary>
    public long StorageUsedBytes   { get; set; }

    /// <summary>Drive storage total in bytes (0 = unlimited).</summary>
    public long StorageTotalBytes  { get; set; }

    public List<SyncPairStatus> PairStatuses { get; set; } = new();

    /// <summary>Aggregate metrics across all engines (nullable if not yet populated).</summary>
    public Services.SyncMetricsSnapshot? Metrics { get; set; }
}

public class SyncPairStatus
{
    public string   SyncPairId        { get; set; } = string.Empty;
    public string   LocalPath         { get; set; } = string.Empty;
    public string   RemoteFolderName  { get; set; } = string.Empty;
    public SyncState State            { get; set; }
    public int      PendingChanges    { get; set; }
    public int      PendingDeletions  { get; set; }
    public int      FailedOperations  { get; set; }
    public int      ExhaustedRetries  { get; set; }
    public string?  LastError         { get; set; }
    public List<string> SelectedSubfolders { get; set; } = new();
}
