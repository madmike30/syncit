using System.Text.Json.Serialization;

namespace SyncIt.Core.Models;

/// <summary>
/// Root configuration object deserialized from %APPDATA%\SyncIt\syncit.json.
/// clientId and clientSecret are NOT stored here — they live in Windows Credential Manager.
/// </summary>
public class SyncConfig
{
    public string TokenStorePath    { get; set; } = @"%APPDATA%\SyncIt\token.bin";
    public string StateDbPath       { get; set; } = @"%APPDATA%\SyncIt\state.db";
    public string DeleteJournalPath { get; set; } = @"%APPDATA%\SyncIt\delete-journal.db";
    public string LogDirectory      { get; set; } = @"%APPDATA%\SyncIt\logs";
    public string LocalTrashPath    { get; set; } = @"%APPDATA%\SyncIt\.syncit-trash";

    /// <summary>How often (in seconds) to poll Google Drive for remote changes.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// If a single sync cycle would delete more than this many files, freeze and require user approval.
    /// Default 10 — prevents catastrophic mass-delete from misconfiguration.
    /// </summary>
    public int MaxDeletionsPerCycle { get; set; } = 10;

    /// <summary>
    /// Number of sync cycles a deletion must wait before executing (grace period).
    /// Default 2 — gives user time to catch accidental deletes before they propagate.
    /// </summary>
    public int DeletionGraceCycles { get; set; } = 2;

    /// <summary>Name of the named pipe used for tray↔service IPC.</summary>
    public string PipeName { get; set; } = "SyncItIpc";

    public string ActivityLogPath      { get; set; } = @"%APPDATA%\SyncIt\activity.db";
    public int    ActivityLogRetentionDays { get; set; } = 30;

    /// <summary>Max upload speed in KB/s. 0 = unlimited.</summary>
    public int MaxUploadKBps { get; set; } = 0;

    /// <summary>Max download speed in KB/s. 0 = unlimited.</summary>
    public int MaxDownloadKBps { get; set; } = 0;

    /// <summary>Retry policy for upload operations. Null = use defaults (5 retries, 1s base, 30s max).</summary>
    public RetryPolicyConfig? UploadRetryPolicy { get; set; }

    /// <summary>Retry policy for download operations. Null = use defaults (5 retries, 1s base, 30s max).</summary>
    public RetryPolicyConfig? DownloadRetryPolicy { get; set; }

    /// <summary>Retry policy for metadata operations. Null = use defaults (3 retries, 500ms base, 5s max).</summary>
    public RetryPolicyConfig? MetadataRetryPolicy { get; set; }

    /// <summary>Max retries per file before marking as exhausted. Default 3.</summary>
    public int MaxFileRetries { get; set; } = 3;

    /// <summary>Base delay in seconds for exponential backoff on file retry. Default 5.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Max delay in seconds for exponential backoff on file retry. Default 300 (5 min).</summary>
    public int RetryMaxDelaySeconds { get; set; } = 300;

    public List<SyncPair> SyncPairs { get; set; } = new();
}
