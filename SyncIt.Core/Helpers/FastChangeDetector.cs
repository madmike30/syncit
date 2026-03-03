using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Determines whether a file needs to be hashed by first comparing mtime and size.
/// This is the rsync pattern: stat cheaply, hash only on mtime/size mismatch.
///
/// On a 10,000-file folder with 5 changed files:
///   Naive approach: hash 10,000 files every cycle → high CPU/IO
///   This approach: stat 10,000 files (cheap), hash only ~5 → near-zero overhead
/// </summary>
public static class FastChangeDetector
{
    /// <summary>
    /// Check if a local file has changed compared to its baseline record.
    /// Returns ChangeResult indicating whether content might have changed.
    /// </summary>
    public static ChangeResult CheckLocalFile(string absolutePath, FileRecord? baseline)
    {
        FileInfo fi;
        try { fi = new FileInfo(absolutePath); }
        catch { return ChangeResult.AccessError; }

        if (!fi.Exists)
            return ChangeResult.Deleted;

        if (baseline is null)
            return ChangeResult.NewFile;

        // Fast path: mtime AND size both match → assume unchanged (no hash needed)
        if (fi.LastWriteTimeUtc == baseline.ModifiedUtc && fi.Length == baseline.SizeBytes)
            return ChangeResult.Unchanged;

        // mtime or size differs → need to hash to confirm
        return ChangeResult.NeedsHash;
    }

    /// <summary>
    /// Build a minimal FileRecord from filesystem metadata (without hashing).
    /// </summary>
    public static (long sizeBytes, DateTime modifiedUtc) GetFileMeta(string absolutePath)
    {
        var fi = new FileInfo(absolutePath);
        return (fi.Length, fi.LastWriteTimeUtc);
    }
}

public enum ChangeResult
{
    Unchanged,    // mtime + size match baseline → skip hashing
    NeedsHash,    // mtime or size changed → hash to confirm content change
    NewFile,      // no baseline record exists
    Deleted,      // file no longer exists
    AccessError,  // could not stat the file (permission denied etc.)
}
