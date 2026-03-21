namespace SyncIt.Core.Models;

/// <summary>
/// User-configurable retry policy parameters for Drive API operations.
/// </summary>
public class RetryPolicyConfig
{
    /// <summary>Number of retry attempts. Default varies by operation type.</summary>
    public int RetryCount { get; set; }

    /// <summary>Base delay in milliseconds for exponential backoff.</summary>
    public int BaseDelayMs { get; set; }

    /// <summary>Maximum delay in milliseconds (cap for exponential backoff).</summary>
    public int MaxDelayMs { get; set; }
}
