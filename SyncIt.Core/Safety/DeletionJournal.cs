using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Safety;

/// <summary>
/// Append-only audit log of all deletions (delete-journal.db).
/// Every deletion is recorded BEFORE execution.
/// Rows are never deleted — this is a permanent audit trail.
///
/// The journal answers: "What did SyncIt delete, when, and why?"
/// </summary>
public sealed class DeletionJournal : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DeletionJournal> _logger;
    private readonly SqliteConnection? _keepAliveConnection;

    public DeletionJournal(string dbPath, ILogger<DeletionJournal> logger)
    {
        var expanded = PathHelper.Expand(dbPath);
        PathHelper.EnsureDirectory(Path.GetDirectoryName(expanded)!);
        _connectionString = $"Data Source={expanded};Cache=Shared;Mode=ReadWriteCreate";
        _logger = logger;
    }

    /// <summary>
    /// Creates an in-memory DeletionJournal for testing.
    /// Uses a named in-memory database so multiple connections share the same data.
    /// </summary>
    internal DeletionJournal(string name, SqliteConnection keepAlive, ILogger<DeletionJournal> logger)
    {
        _keepAliveConnection = keepAlive;
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS deletion_records (
                id             TEXT PRIMARY KEY,
                sync_pair_id   TEXT NOT NULL,
                relative_path  TEXT NOT NULL,
                side           INTEGER NOT NULL,
                drive_file_id  TEXT,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                modified_utc   TEXT NOT NULL,
                scheduled_utc  TEXT NOT NULL,
                executed_utc   TEXT,
                trigger_reason TEXT NOT NULL,
                status         INTEGER NOT NULL,
                trash_path     TEXT,
                grace_cycles   INTEGER NOT NULL DEFAULT 0
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_del_status
            ON deletion_records (status, scheduled_utc);

            CREATE INDEX IF NOT EXISTS idx_del_pair
            ON deletion_records (sync_pair_id, status);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("DeletionJournal initialized.");
    }

    /// <summary>Record a new deletion before it is executed.</summary>
    public async Task RecordAsync(DeletionRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO deletion_records
                (id, sync_pair_id, relative_path, side, drive_file_id, size_bytes,
                 modified_utc, scheduled_utc, executed_utc, trigger_reason, status, trash_path, grace_cycles)
            VALUES
                (@id, @pair, @path, @side, @driveId, @size, @mtime, @sched, @exec,
                 @reason, @status, @trash, @grace);
            """;
        AddParams(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation(
            "Deletion recorded: {Path} (side={Side}, status={Status}, reason={Reason})",
            record.RelativePath, record.Side, record.Status, record.TriggerReason);
    }

    /// <summary>Update the status (and optional executed time / trash path) of an existing record.</summary>
    public async Task UpdateStatusAsync(
        string id,
        DeletionStatus status,
        DateTime? executedUtc = null,
        string? trashPath = null,
        bool resetGraceCycles = false,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = resetGraceCycles
            ? """
              UPDATE deletion_records
              SET status=@status, executed_utc=@exec, trash_path=@trash, grace_cycles=0
              WHERE id=@id;
              """
            : """
              UPDATE deletion_records
              SET status=@status, executed_utc=@exec, trash_path=@trash
              WHERE id=@id;
              """;
        cmd.Parameters.AddWithValue("@id",     id);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@exec",   (object?)executedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@trash",  (object?)trashPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Freeze ALL pending deletions for a pair (used when freeze threshold is exceeded).</summary>
    public async Task<int> FreezeAllPendingAsync(string syncPairId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE deletion_records
            SET status=@frozen
            WHERE sync_pair_id=@p AND status=@pending;
            """;
        cmd.Parameters.AddWithValue("@p",       syncPairId);
        cmd.Parameters.AddWithValue("@pending", (int)DeletionStatus.Pending);
        cmd.Parameters.AddWithValue("@frozen",  (int)DeletionStatus.Frozen);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Update the grace cycle counter on a pending deletion.</summary>
    public async Task DecrementGraceCycleAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE deletion_records SET grace_cycles = grace_cycles - 1 WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Tick down grace cycles for ALL pending deletions for a pair (called once per sync cycle).
    /// Stops at 0 — items at 0 are picked up by GetReadyToExecuteAsync on the same cycle.
    /// </summary>
    public async Task DecrementAllPendingGraceCyclesAsync(string syncPairId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE deletion_records
            SET grace_cycles = grace_cycles - 1
            WHERE sync_pair_id=@p AND status=@pending AND grace_cycles > 0;
            """;
        cmd.Parameters.AddWithValue("@p",       syncPairId);
        cmd.Parameters.AddWithValue("@pending", (int)DeletionStatus.Pending);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get all pending (Pending) deletions for a pair that have exhausted their grace period.</summary>
    public async IAsyncEnumerable<DeletionRecord> GetReadyToExecuteAsync(
        string syncPairId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM deletion_records
            WHERE sync_pair_id=@p AND status=@pending AND grace_cycles <= 0
            ORDER BY scheduled_utc;
            """;
        cmd.Parameters.AddWithValue("@p",       syncPairId);
        cmd.Parameters.AddWithValue("@pending", (int)DeletionStatus.Pending);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadRecord(reader);
    }

    /// <summary>Count pending + frozen (not yet executed) deletions for a pair.</summary>
    public async Task<int> CountPendingAsync(string syncPairId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM deletion_records
            WHERE sync_pair_id=@p AND status IN (@pending, @frozen);
            """;
        cmd.Parameters.AddWithValue("@p",       syncPairId);
        cmd.Parameters.AddWithValue("@pending", (int)DeletionStatus.Pending);
        cmd.Parameters.AddWithValue("@frozen",  (int)DeletionStatus.Frozen);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>Count only active Pending deletions (not Frozen) for threshold checks.</summary>
    public async Task<int> CountActivePendingAsync(string syncPairId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM deletion_records
            WHERE sync_pair_id=@p AND status=@pending;
            """;
        cmd.Parameters.AddWithValue("@p",       syncPairId);
        cmd.Parameters.AddWithValue("@pending", (int)DeletionStatus.Pending);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>Get all frozen deletions for a pair (awaiting user approval).</summary>
    public async IAsyncEnumerable<DeletionRecord> GetFrozenAsync(
        string syncPairId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM deletion_records
            WHERE sync_pair_id=@p AND status=@frozen ORDER BY scheduled_utc;
            """;
        cmd.Parameters.AddWithValue("@p",      syncPairId);
        cmd.Parameters.AddWithValue("@frozen", (int)DeletionStatus.Frozen);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadRecord(reader);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool _pragmasSet;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (!_pragmasSet && _keepAliveConnection is null)
        {
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct);
            _pragmasSet = true;
        }

        return conn;
    }

    private static void AddParams(SqliteCommand cmd, DeletionRecord r)
    {
        cmd.Parameters.AddWithValue("@id",     r.Id);
        cmd.Parameters.AddWithValue("@pair",   r.SyncPairId);
        cmd.Parameters.AddWithValue("@path",   r.RelativePath);
        cmd.Parameters.AddWithValue("@side",   (int)r.Side);
        cmd.Parameters.AddWithValue("@driveId",(object?)r.DriveFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size",   r.SizeBytes);
        cmd.Parameters.AddWithValue("@mtime",  r.ModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@sched",  r.ScheduledUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@exec",   (object?)r.ExecutedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reason", r.TriggerReason);
        cmd.Parameters.AddWithValue("@status", (int)r.Status);
        cmd.Parameters.AddWithValue("@trash",  (object?)r.TrashPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@grace",  r.GraceCyclesRemaining);
    }

    private static DeletionRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id            = r.GetString(r.GetOrdinal("id")),
        SyncPairId    = r.GetString(r.GetOrdinal("sync_pair_id")),
        RelativePath  = r.GetString(r.GetOrdinal("relative_path")),
        Side          = (SyncSide)r.GetInt32(r.GetOrdinal("side")),
        DriveFileId   = r.IsDBNull(r.GetOrdinal("drive_file_id")) ? null : r.GetString(r.GetOrdinal("drive_file_id")),
        SizeBytes     = r.GetInt64(r.GetOrdinal("size_bytes")),
        ModifiedUtc   = DateTime.Parse(r.GetString(r.GetOrdinal("modified_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        ScheduledUtc  = DateTime.Parse(r.GetString(r.GetOrdinal("scheduled_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        ExecutedUtc   = r.IsDBNull(r.GetOrdinal("executed_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("executed_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        TriggerReason = r.GetString(r.GetOrdinal("trigger_reason")),
        Status        = (DeletionStatus)r.GetInt32(r.GetOrdinal("status")),
        TrashPath     = r.IsDBNull(r.GetOrdinal("trash_path")) ? null : r.GetString(r.GetOrdinal("trash_path")),
        GraceCyclesRemaining = r.GetInt32(r.GetOrdinal("grace_cycles")),
    };

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }
}
