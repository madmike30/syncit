namespace SyncIt.Core.Models;

/// <summary>One local↔remote folder mapping with its own conflict policy and filters.</summary>
public class SyncPair
{
    /// <summary>Stable unique ID for this pair. Used as primary key in all databases.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Absolute local folder path, e.g. C:\Users\Me\Documents\SyncedDocs</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>Google Drive folder ID. Default "root" = My Drive root.</summary>
    public string RemoteFolderId { get; set; } = "root";

    /// <summary>Human-readable name for display purposes only.</summary>
    public string RemoteFolderName { get; set; } = "My Drive";

    public bool Enabled { get; set; } = true;

    /// <summary>Google account this pair syncs with (<see cref="GoogleAccount.Id"/>). Null = the first account (pre-1.3 configs).</summary>
    public string? AccountId { get; set; }

    /// <summary>Shared drive ID when the remote folder lives in a shared drive; null = My Drive.</summary>
    public string? SharedDriveId { get; set; }

    /// <summary>Paused pairs keep their state but do not sync until resumed.</summary>
    public bool Paused { get; set; }

    /// <summary>Convert Google Docs/Sheets/Slides to Office formats when downloading.</summary>
    public bool ExportGoogleDocs { get; set; } = true;

    public ConflictPolicyOptions ConflictPolicy { get; set; } = new();

    /// <summary>
    /// Glob patterns to exclude. Matched against relative paths.
    /// Examples: "*.tmp", "~$*", "node_modules/**", ".git/**"
    /// </summary>
    public List<string> ExcludeGlobs { get; set; } = new()
    {
        "*.tmp", "*.~*", "~$*", "Thumbs.db", ".DS_Store",
        "node_modules/**", ".git/**", "desktop.ini", "*.syncit-tmp-*"
    };

    /// <summary>
    /// Glob patterns to include. Empty list means include everything (minus excludes).
    /// </summary>
    public List<string> IncludeGlobs { get; set; } = new();

    /// <summary>Skip files larger than this. Default 5GB.</summary>
    public long MaxFileSizeBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Selective subfolder sync.
    /// List of subfolder paths (relative to RemoteFolderId) to include.
    /// Empty list = sync everything inside RemoteFolderId.
    ///
    /// Examples:
    ///   ["Projects"]                 → only sync the Projects/ subtree
    ///   ["Work/Reports", "Finance"]  → sync Work/Reports/ and Finance/ subtrees
    ///
    /// Paths are matched case-insensitively and use forward slashes.
    /// When non-empty, files at the root of RemoteFolderId that are not inside
    /// a selected subfolder are excluded from sync.
    /// </summary>
    public List<string> SelectedSubfolders { get; set; } = new();
}
