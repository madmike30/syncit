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

    /// <summary>True when the user chose a specific remote folder as the pair root (1.4+); never migrated to the drive root.</summary>
    public bool RemoteFolderPinned { get; set; }

    /// <summary>Export format for Google Docs: "docx" (default), "odt", "pdf", "rtf", "txt".</summary>
    public string DocsExportFormat { get; set; } = "docx";
    /// <summary>Export format for Google Sheets: "xlsx" (default), "ods", "pdf", "csv".</summary>
    public string SheetsExportFormat { get; set; } = "xlsx";
    /// <summary>Export format for Google Slides: "pptx" (default), "odp", "pdf".</summary>
    public string SlidesExportFormat { get; set; } = "pptx";
    /// <summary>Mark exported Google files read-only on this PC (default). They are never uploaded back.</summary>
    public bool ExportReadOnly { get; set; } = true;

    /// <summary>Volume serial of the disk this folder lives on (removable/external drives, 1.4+). Null = any.</summary>
    public string? VolumeSerial { get; set; }

    /// <summary>Encrypted backup folder (Experimental, 1.4+): files are encrypted on this PC before upload.</summary>
    public bool Encrypted { get; set; }
    /// <summary>Encrypted folder: PBKDF2 salt (base64). Not secret.</summary>
    public string? EncryptionSalt { get; set; }
    /// <summary>Encrypted folder: passphrase verifier (base64 HMAC). Not the key.</summary>
    public string? EncryptionVerifier { get; set; }
    public int EncryptionIterations { get; set; } = 600_000;

    /// <summary>Paused pairs keep their state but do not sync until resumed.</summary>
    public bool Paused { get; set; }

    /// <summary>Two-way sync (default) or one-way backup to Drive (1.4+).</summary>
    public SyncMode Mode { get; set; } = SyncMode.TwoWay;

    /// <summary>Backup mode only: when true (default) files deleted on this PC stay on Drive.</summary>
    public bool BackupKeepDeletedOnDrive { get; set; } = true;

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

/// <summary>Direction of a sync pair.</summary>
public enum SyncMode
{
    /// <summary>Changes flow both ways.</summary>
    TwoWay,
    /// <summary>PC → Drive only. Nothing on the PC is ever downloaded, overwritten or deleted.</summary>
    BackupOnly,
}
