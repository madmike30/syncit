using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// SQLite-backed baseline state store.
/// Uses raw ADO.NET (no EF Core) for minimal memory footprint.
/// All enumeration is streaming via IAsyncEnumerable.
/// </summary>
public sealed class FileStateStore : IFileStateStore
{
    private readonly string _connectionString;
    private readonly ILogger<FileStateStore> _logger;

    // Kept alive so named in-memory databases persist across connections.
    private readonly SqliteConnection? _keepAliveConnection;

    public FileStateStore(string dbPath, ILogger<FileStateStore> logger)
    {
        var expanded = PathHelper.Expand(dbPath);
        PathHelper.EnsureDirectory(Path.GetDirectoryName(expanded)!);
        _connectionString = $"Data Source={expanded};Cache=Shared;Mode=ReadWriteCreate";
        _logger = logger;
    }

    /// <summary>
    /// Creates an in-memory FileStateStore for testing.
    /// Uses a named in-memory database so multiple connections share the same data.
    /// The keepAlive connection prevents the database from being garbage-collected.
    /// </summary>
    internal FileStateStore(string name, SqliteConnection keepAlive, ILogger<FileStateStore> logger)
    {
        _keepAliveConnection = keepAlive;
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS file_records (
                sync_pair_id   TEXT NOT NULL,
                side           INTEGER NOT NULL,
                relative_path  TEXT NOT NULL,
                drive_file_id  TEXT,
                drive_revision TEXT,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                modified_utc   TEXT NOT NULL,
                content_hash   TEXT,
                is_deleted     INTEGER NOT NULL DEFAULT 0,
                mime_type      TEXT,
                recorded_utc   TEXT NOT NULL,
                PRIMARY KEY (sync_pair_id, side, relative_path)
            ) STRICT;
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE INDEX IF NOT EXISTS idx_records_pair_side
            ON file_records (sync_pair_id, side);
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS pair_state (
                sync_pair_id             TEXT PRIMARY KEY,
                drive_page_token         TEXT,
                last_sync_utc            TEXT,
                pending_deletion_cycles  INTEGER NOT NULL DEFAULT 0
            ) STRICT;
            """, ct);

        _logger.LogDebug("FileStateStore initialized.");
    }

    // -------------------------------------------------------------------------
    // Single-record operations
    // -------------------------------------------------------------------------

    public async Task<FileRecord?> GetRecordAsync(string syncPairId, SyncSide side, string relativePath)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM file_records WHERE sync_pair_id=@p AND side=@s AND relative_path=@r LIMIT 1";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@s", (int)side);
        cmd.Parameters.AddWithValue("@r", relativePath);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRecord(reader) : null;
    }

    public async Task UpsertRecordAsync(FileRecord record)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_records
                (sync_pair_id, side, relative_path, drive_file_id, drive_revision,
                 size_bytes, modified_utc, content_hash, is_deleted, mime_type, recorded_utc)
            VALUES
                (@pair, @side, @path, @driveId, @rev, @size, @mtime, @hash, @deleted, @mime, @rec)
            ON CONFLICT(sync_pair_id, side, relative_path) DO UPDATE SET
                drive_file_id  = excluded.drive_file_id,
                drive_revision = excluded.drive_revision,
                size_bytes     = excluded.size_bytes,
                modified_utc   = excluded.modified_utc,
                content_hash   = excluded.content_hash,
                is_deleted     = excluded.is_deleted,
                mime_type      = excluded.mime_type,
                recorded_utc   = excluded.recorded_utc;
            """;
        AddRecordParams(cmd, record);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateMtimeAsync(string syncPairId, SyncSide side, string relativePath, DateTime newMtime)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE file_records SET modified_utc=@mtime, recorded_utc=@rec WHERE sync_pair_id=@p AND side=@s AND relative_path=@r";
        cmd.Parameters.AddWithValue("@mtime", newMtime.ToString("O"));
        cmd.Parameters.AddWithValue("@rec",   DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@p",     syncPairId);
        cmd.Parameters.AddWithValue("@s",     (int)side);
        cmd.Parameters.AddWithValue("@r",     relativePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkDeletedAsync(string syncPairId, SyncSide side, string relativePath)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE file_records SET is_deleted=1, recorded_utc=@rec WHERE sync_pair_id=@p AND side=@s AND relative_path=@r";
        cmd.Parameters.AddWithValue("@rec", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@p",   syncPairId);
        cmd.Parameters.AddWithValue("@s",   (int)side);
        cmd.Parameters.AddWithValue("@r",   relativePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveRecordAsync(string syncPairId, SyncSide side, string relativePath)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM file_records WHERE sync_pair_id=@p AND side=@s AND relative_path=@r";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@s", (int)side);
        cmd.Parameters.AddWithValue("@r", relativePath);
        await cmd.ExecuteNonQueryAsync();
    }

    // -------------------------------------------------------------------------
    // Streaming enumeration
    // -------------------------------------------------------------------------

    public async IAsyncEnumerable<FileRecord> StreamRecordsAsync(
        string syncPairId,
        SyncSide side,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM file_records WHERE sync_pair_id=@p AND side=@s AND is_deleted=0 ORDER BY relative_path";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@s", (int)side);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadRecord(reader);
    }

    public async Task<int> CountRecordsAsync(string syncPairId, SyncSide side)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM file_records WHERE sync_pair_id=@p AND side=@s AND is_deleted=0";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@s", (int)side);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    // -------------------------------------------------------------------------
    // Pair-level state
    // -------------------------------------------------------------------------

    public async Task<string?> GetPageTokenAsync(string syncPairId)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT drive_page_token FROM pair_state WHERE sync_pair_id=@p";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SavePageTokenAsync(string syncPairId, string pageToken)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pair_state (sync_pair_id, drive_page_token)
            VALUES (@p, @token)
            ON CONFLICT(sync_pair_id) DO UPDATE SET drive_page_token=excluded.drive_page_token;
            """;
        cmd.Parameters.AddWithValue("@p",     syncPairId);
        cmd.Parameters.AddWithValue("@token", pageToken);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DateTime?> GetLastSyncTimeAsync(string syncPairId)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT last_sync_utc FROM pair_state WHERE sync_pair_id=@p";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : null;
    }

    public async Task SetLastSyncTimeAsync(string syncPairId, DateTime syncTimeUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pair_state (sync_pair_id, last_sync_utc)
            VALUES (@p, @t)
            ON CONFLICT(sync_pair_id) DO UPDATE SET last_sync_utc=excluded.last_sync_utc;
            """;
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@t", syncTimeUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetPendingDeletionCyclesAsync(string syncPairId)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT pending_deletion_cycles FROM pair_state WHERE sync_pair_id=@p";
        cmd.Parameters.AddWithValue("@p", syncPairId);
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : 0;
    }

    public async Task SetPendingDeletionCyclesAsync(string syncPairId, int cycles)
    {
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pair_state (sync_pair_id, pending_deletion_cycles)
            VALUES (@p, @c)
            ON CONFLICT(sync_pair_id) DO UPDATE SET pending_deletion_cycles=excluded.pending_deletion_cycles;
            """;
        cmd.Parameters.AddWithValue("@p", syncPairId);
        cmd.Parameters.AddWithValue("@c", cycles);
        await cmd.ExecuteNonQueryAsync();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (_keepAliveConnection is null)
        {
            // Enable WAL mode for file-based databases
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        return conn;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FileRecord ReadRecord(SqliteDataReader r) => new()
    {
        SyncPairId    = r.GetString(r.GetOrdinal("sync_pair_id")),
        Side          = (SyncSide)r.GetInt32(r.GetOrdinal("side")),
        RelativePath  = r.GetString(r.GetOrdinal("relative_path")),
        DriveFileId   = r.IsDBNull(r.GetOrdinal("drive_file_id"))   ? null : r.GetString(r.GetOrdinal("drive_file_id")),
        DriveRevisionId = r.IsDBNull(r.GetOrdinal("drive_revision")) ? null : r.GetString(r.GetOrdinal("drive_revision")),
        SizeBytes     = r.GetInt64(r.GetOrdinal("size_bytes")),
        ModifiedUtc   = DateTime.Parse(r.GetString(r.GetOrdinal("modified_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        ContentHash   = r.IsDBNull(r.GetOrdinal("content_hash")) ? null : r.GetString(r.GetOrdinal("content_hash")),
        IsDeleted     = r.GetInt32(r.GetOrdinal("is_deleted")) == 1,
        MimeType      = r.IsDBNull(r.GetOrdinal("mime_type"))   ? null : r.GetString(r.GetOrdinal("mime_type")),
        RecordedUtc   = DateTime.Parse(r.GetString(r.GetOrdinal("recorded_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static void AddRecordParams(SqliteCommand cmd, FileRecord r)
    {
        cmd.Parameters.AddWithValue("@pair",    r.SyncPairId);
        cmd.Parameters.AddWithValue("@side",    (int)r.Side);
        cmd.Parameters.AddWithValue("@path",    r.RelativePath);
        cmd.Parameters.AddWithValue("@driveId", (object?)r.DriveFileId   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rev",     (object?)r.DriveRevisionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size",    r.SizeBytes);
        cmd.Parameters.AddWithValue("@mtime",   r.ModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@hash",    (object?)r.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deleted", r.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@mime",    (object?)r.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rec",     r.RecordedUtc.ToString("O"));
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }
}
