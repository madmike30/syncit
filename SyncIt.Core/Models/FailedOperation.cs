namespace SyncIt.Core.Models;

/// <summary>
/// Tracks a sync operation that failed and may be retried with exponential backoff.
/// </summary>
public class FailedOperation
{
    public ChangeEvent OriginalEvent { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTime LastAttemptUtc { get; set; }
    public DateTime NextRetryUtc { get; set; }
    public string LastError { get; set; } = string.Empty;

    /// <summary>True when AttemptCount >= MaxFileRetries — no more retries.</summary>
    public bool IsExhausted { get; set; }
}
