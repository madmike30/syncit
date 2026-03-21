using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Persistent storage for the baseline file snapshots used by the three-snapshot diff algorithm.
/// Backed by SQLite (state.db). All operations stream rather than loading full collections.
/// </summary>
public interface IFileStateStore : IDisposable
{
    /// <summary>Initialize the database schema. Call once at startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Single-record lookups (used by fast-path change detection)
    // -------------------------------------------------------------------------

    /// <summary>Get the baseline record for a specific file on a specific side.</summary>
    Task<FileRecord?> GetRecordAsync(string syncPairId, SyncSide side, string relativePath);

    /// <summary>Upsert a file record into the baseline.</summary>
    Task UpsertRecordAsync(FileRecord record);

    /// <summary>Update only the mtime of an existing record (content unchanged).</summary>
    Task UpdateMtimeAsync(string syncPairId, SyncSide side, string relativePath, DateTime newMtime);

    /// <summary>Mark a file as deleted in the baseline (sets is_deleted=1).</summary>
    Task MarkDeletedAsync(string syncPairId, SyncSide side, string relativePath);

    /// <summary>Remove a record entirely from the baseline.</summary>
    Task RemoveRecordAsync(string syncPairId, SyncSide side, string relativePath);

    /// <summary>Find a record by Drive file ID (uses indexed lookup instead of full scan).</summary>
    Task<FileRecord?> GetRecordByDriveFileIdAsync(string syncPairId, SyncSide side, string driveFileId);

    // -------------------------------------------------------------------------
    // Streaming enumeration (used during sync cycles — never ToList())
    // -------------------------------------------------------------------------

    /// <summary>Stream all baseline records for a pair/side combination.</summary>
    IAsyncEnumerable<FileRecord> StreamRecordsAsync(string syncPairId, SyncSide side, CancellationToken ct = default);

    /// <summary>Count baseline records for a pair/side (fast COUNT(*) query).</summary>
    Task<int> CountRecordsAsync(string syncPairId, SyncSide side);

    // -------------------------------------------------------------------------
    // Pair-level state (page tokens, sync timestamps)
    // -------------------------------------------------------------------------

    /// <summary>Get the Drive Changes API page token for a pair.</summary>
    Task<string?> GetPageTokenAsync(string syncPairId);

    /// <summary>Save the Drive Changes API page token for a pair.</summary>
    Task SavePageTokenAsync(string syncPairId, string pageToken);

    /// <summary>Get the last successful sync time for a pair.</summary>
    Task<DateTime?> GetLastSyncTimeAsync(string syncPairId);

    /// <summary>Update the last successful sync time for a pair.</summary>
    Task SetLastSyncTimeAsync(string syncPairId, DateTime syncTimeUtc);

    /// <summary>Get the pending deletion grace cycle count for a pair.</summary>
    Task<int> GetPendingDeletionCyclesAsync(string syncPairId);

    /// <summary>Set the pending deletion grace cycle count for a pair.</summary>
    Task SetPendingDeletionCyclesAsync(string syncPairId, int cycles);
}
