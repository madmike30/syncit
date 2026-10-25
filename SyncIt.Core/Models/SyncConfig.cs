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
    /// If the total pending deletions exceeds this threshold, freeze and require user approval.
    /// Default 50 — prevents catastrophic mass-delete from misconfiguration while allowing
    /// normal batch operations.
    /// </summary>
    public int MaxDeletionsPerCycle { get; set; } = 50;

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

    /// <summary>Signed-in Google accounts (1.3+). Each has its own DPAPI-encrypted token file.</summary>
    public List<GoogleAccount> Accounts { get; set; } = new();

    /// <summary>Check GitHub Releases for new versions (1.3+). Users can turn this off in Settings.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Only sync during active hours (1.4+). Outside them sync is paused.</summary>
    public bool ActiveHoursEnabled { get; set; }

    /// <summary>Active hours start (local hour 0-23, inclusive).</summary>
    public int ActiveHoursStart { get; set; } = 8;

    /// <summary>Active hours end (local hour 0-23, exclusive). End before start wraps past midnight.</summary>
    public int ActiveHoursEnd { get; set; } = 23;

    /// <summary>Windows notification when a conflict is resolved (1.4+).</summary>
    public bool NotifyConflicts { get; set; } = true;

    /// <summary>Windows notification when files are deleted or mass-deletion protection triggers (1.4+).</summary>
    public bool NotifyDeletions { get; set; } = true;
}
