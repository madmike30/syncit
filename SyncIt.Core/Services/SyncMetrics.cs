using System.Diagnostics;

namespace SyncIt.Core.Services;

/// <summary>
/// Thread-safe sync metrics using Interlocked counters.
/// Tracks bytes transferred, API calls, sync cycles, errors, and cycle durations.
/// </summary>
public sealed class SyncMetrics
{
    private long _totalBytesUploaded;
    private long _totalBytesDownloaded;
    private long _totalApiCalls;
    private long _totalSyncCycles;
    private long _totalErrors;
    private long _lastCycleDurationMs;
    private readonly Stopwatch _cycleStopwatch = new();

    public long TotalBytesUploaded => Interlocked.Read(ref _totalBytesUploaded);
    public long TotalBytesDownloaded => Interlocked.Read(ref _totalBytesDownloaded);
    public long TotalApiCalls => Interlocked.Read(ref _totalApiCalls);
    public long TotalSyncCycles => Interlocked.Read(ref _totalSyncCycles);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public long LastCycleDurationMs => Interlocked.Read(ref _lastCycleDurationMs);

    public void RecordUpload(long bytes) => Interlocked.Add(ref _totalBytesUploaded, bytes);
    public void RecordDownload(long bytes) => Interlocked.Add(ref _totalBytesDownloaded, bytes);
    public void RecordApiCall() => Interlocked.Increment(ref _totalApiCalls);
    public void RecordError() => Interlocked.Increment(ref _totalErrors);

    public void StartCycle() => _cycleStopwatch.Restart();

    public void EndCycle()
    {
        _cycleStopwatch.Stop();
        Interlocked.Exchange(ref _lastCycleDurationMs, _cycleStopwatch.ElapsedMilliseconds);
        Interlocked.Increment(ref _totalSyncCycles);
    }

    /// <summary>Returns a snapshot of current metrics for IPC serialization.</summary>
    public SyncMetricsSnapshot TakeSnapshot() => new()
    {
        TotalBytesUploaded   = TotalBytesUploaded,
        TotalBytesDownloaded = TotalBytesDownloaded,
        TotalApiCalls        = TotalApiCalls,
        TotalSyncCycles      = TotalSyncCycles,
        TotalErrors          = TotalErrors,
        LastCycleDurationMs  = LastCycleDurationMs,
    };
}

/// <summary>Serializable snapshot of SyncMetrics for IPC / status display.</summary>
public class SyncMetricsSnapshot
{
    public long TotalBytesUploaded   { get; set; }
    public long TotalBytesDownloaded { get; set; }
    public long TotalApiCalls        { get; set; }
    public long TotalSyncCycles      { get; set; }
    public long TotalErrors          { get; set; }
    public long LastCycleDurationMs  { get; set; }
}
