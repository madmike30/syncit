using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Persistent sync activity history backed by SQLite (activity.db).
/// Records uploads, downloads, conflicts, deletions, and errors with timestamps.
/// </summary>
public interface IActivityLog : IDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task RecordAsync(ActivityRecord record, CancellationToken ct = default);
    Task<List<ActivityRecord>> GetRecentAsync(int limit = 100, string? syncPairId = null, CancellationToken ct = default);
    Task PruneAsync(int maxAgeDays = 30, CancellationToken ct = default);
}

public sealed class ActivityLog : IActivityLog
{
    private readonly string _connectionString;
    private readonly ILogger<ActivityLog> _logger;
    private readonly SqliteConnection? _keepAliveConnection;

    public ActivityLog(string dbPath, ILogger<ActivityLog> logger)
    {
        var expanded = PathHelper.Expand(dbPath);
        PathHelper.EnsureDirectory(Path.GetDirectoryName(expanded)!);
        _connectionString = $"Data Source={expanded};Cache=Shared;Mode=ReadWriteCreate";
        _logger = logger;
    }

    /// <summary>In-memory constructor for testing.</summary>
    internal ActivityLog(string name, SqliteConnection keepAlive, ILogger<ActivityLog> logger)
    {
        _keepAliveConnection = keepAlive;
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS activity_records (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_pair_id   TEXT NOT NULL,
                type           INTEGER NOT NULL,
                relative_path  TEXT NOT NULL,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                timestamp_utc  TEXT NOT NULL,
                details        TEXT
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_activity_time
            ON activity_records (timestamp_utc DESC);

            CREATE INDEX IF NOT EXISTS idx_activity_pair
            ON activity_records (sync_pair_id, timestamp_utc DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("ActivityLog initialized.");
    }

    public async Task RecordAsync(ActivityRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity_records (sync_pair_id, type, relative_path, size_bytes, timestamp_utc, details)
            VALUES (@pair, @type, @path, @size, @time, @details);
            """;
        cmd.Parameters.AddWithValue("@pair", record.SyncPairId);
        cmd.Parameters.AddWithValue("@type", (int)record.Type);
        cmd.Parameters.AddWithValue("@path", record.RelativePath);
        cmd.Parameters.AddWithValue("@size", record.SizeBytes);
        cmd.Parameters.AddWithValue("@time", record.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@details", (object?)record.Details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ActivityRecord>> GetRecentAsync(
        int limit = 100,
        string? syncPairId = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (syncPairId is not null)
        {
            cmd.CommandText = "SELECT * FROM activity_records WHERE sync_pair_id=@pair ORDER BY timestamp_utc DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@pair", syncPairId);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM activity_records ORDER BY timestamp_utc DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ActivityRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ActivityRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SyncPairId = reader.GetString(reader.GetOrdinal("sync_pair_id")),
                Type = (ActivityType)reader.GetInt32(reader.GetOrdinal("type")),
                RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
                TimestampUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp_utc")),
                    null, System.Globalization.DateTimeStyles.RoundtripKind),
                Details = reader.IsDBNull(reader.GetOrdinal("details"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("details")),
            });
        }

        return results;
    }

    public async Task PruneAsync(int maxAgeDays = 30, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).ToString("O");
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM activity_records WHERE timestamp_utc < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        if (deleted > 0)
            _logger.LogInformation("Pruned {Count} activity records older than {Days} days.", deleted, maxAgeDays);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (_keepAliveConnection is null)
        {
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        return conn;
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }
}
