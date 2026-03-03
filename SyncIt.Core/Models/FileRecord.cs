namespace SyncIt.Core.Models;

public enum SyncSide
{
    Local  = 0,
    Remote = 1,
}

/// <summary>
/// Snapshot of one file's known state on a specific side (local or remote).
/// Stored in state.db as the baseline for change detection.
/// </summary>
public class FileRecord
{
    public string SyncPairId   { get; set; } = string.Empty;
    public SyncSide Side       { get; set; }
    public string RelativePath { get; set; } = string.Empty;

    // Remote-side fields (null for local records)
    public string? DriveFileId   { get; set; }
    public string? DriveRevisionId { get; set; }

    public long     SizeBytes    { get; set; }
    public DateTime ModifiedUtc  { get; set; }

    /// <summary>
    /// MD5 hash — matches what Google Drive stores, enabling direct comparison
    /// without re-hashing. Null for Google Docs (they have no MD5).
    /// </summary>
    public string? ContentHash   { get; set; }

    public bool    IsDeleted     { get; set; }
    public string? MimeType      { get; set; }
    public DateTime RecordedUtc  { get; set; } = DateTime.UtcNow;
}
