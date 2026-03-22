using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Tracks failed sync operations and schedules retries with exponential backoff.
/// Thread-safe — designed for concurrent access from consumer and poll loops.
/// </summary>
public sealed class FailedOperationTracker
{
    private readonly SyncConfig _config;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FailedOperation> _failures = new();

    public FailedOperationTracker(SyncConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    private int _failedCount;
    private int _exhaustedCount;

    /// <summary>Number of operations that can still be retried.</summary>
    public int FailedCount => Volatile.Read(ref _failedCount);

    /// <summary>Number of operations that have exhausted all retries.</summary>
    public int ExhaustedCount => Volatile.Read(ref _exhaustedCount);

    /// <summary>
    /// Record a failure for a change event. Increments attempt count and schedules next retry.
    /// </summary>
    public void RecordFailure(ChangeEvent ev, string errorMessage)
    {
        var key = MakeKey(ev);

        _failures.AddOrUpdate(key,
            _ =>
            {
                var op = new FailedOperation
                {
                    OriginalEvent = ev,
                    AttemptCount = 1,
                    LastAttemptUtc = DateTime.UtcNow,
                    LastError = errorMessage,
                };
                op.NextRetryUtc = ComputeNextRetry(op.AttemptCount);
                op.IsExhausted = op.AttemptCount >= _config.MaxFileRetries;
                return op;
            },
            (_, existing) =>
            {
                existing.AttemptCount++;
                existing.LastAttemptUtc = DateTime.UtcNow;
                existing.LastError = errorMessage;
                existing.NextRetryUtc = ComputeNextRetry(existing.AttemptCount);
                existing.IsExhausted = existing.AttemptCount >= _config.MaxFileRetries;
                return existing;
            });

        var failure = _failures[key];
        RecalculateCounts();
        if (failure.IsExhausted)
            _logger.LogWarning("Operation exhausted retries ({Count}/{Max}): {Kind} '{Path}'",
                failure.AttemptCount, _config.MaxFileRetries, ev.Kind, ev.RelativePath);
        else
            _logger.LogDebug("Recorded failure #{Count} for {Kind} '{Path}', next retry at {Next:HH:mm:ss}",
                failure.AttemptCount, ev.Kind, ev.RelativePath, failure.NextRetryUtc.ToLocalTime());
    }

    /// <summary>
    /// Mark an operation as successful, removing it from the failure tracker.
    /// </summary>
    public void MarkSuccess(ChangeEvent ev)
    {
        if (_failures.TryRemove(MakeKey(ev), out _))
            RecalculateCounts();
    }

    /// <summary>
    /// Returns change events that are ready to be retried (NextRetryUtc <= now and not exhausted).
    /// </summary>
    public List<ChangeEvent> GetRetryableEvents()
    {
        var now = DateTime.UtcNow;
        return _failures.Values
            .Where(f => !f.IsExhausted && f.NextRetryUtc <= now)
            .Select(f => f.OriginalEvent)
            .ToList();
    }

    /// <summary>
    /// Check if a specific event has exhausted its retries.
    /// </summary>
    public bool IsExhausted(ChangeEvent ev)
    {
        return _failures.TryGetValue(MakeKey(ev), out var op) && op.IsExhausted;
    }

    /// <summary>
    /// Get a snapshot of all failed operations for status reporting.
    /// </summary>
    public List<FailedOperation> GetSnapshot() => _failures.Values.ToList();

    private void RecalculateCounts()
    {
        int failed = 0, exhausted = 0;
        foreach (var f in _failures.Values)
        {
            if (f.IsExhausted) exhausted++;
            else failed++;
        }
        Volatile.Write(ref _failedCount, failed);
        Volatile.Write(ref _exhaustedCount, exhausted);
    }

    private DateTime ComputeNextRetry(int attemptCount)
    {
        // Exponential backoff: baseDelay * 2^(attempt-1), capped at maxDelay
        var delaySec = _config.RetryBaseDelaySeconds * Math.Pow(2, attemptCount - 1);
        delaySec = Math.Min(delaySec, _config.RetryMaxDelaySeconds);
        return DateTime.UtcNow.AddSeconds(delaySec);
    }

    private static string MakeKey(ChangeEvent ev) =>
        $"{ev.SyncPairId}|{ev.Side}|{ev.Kind}|{ev.RelativePath}";
}
