using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Polly-based retry policies for Google Drive API calls.
/// Handles transient failures: 429 (rate limit), 503 (unavailable), network errors.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Standard retry policy for Drive API operations.
    /// Defaults: 5 retries with exponential backoff + jitter: ~1s, 2s, 4s, 8s, 16s.
    /// </summary>
    public static AsyncRetryPolicy CreateDrivePolicy(ILogger logger, RetryPolicyConfig? config = null)
        => CreateRetry(logger, config);

    /// <summary>
    /// Retry policy where every single attempt is also bounded by <paramref name="attemptTimeout"/>.
    /// A request that hangs (half-open connection after a network drop) is abandoned and retried
    /// instead of blocking the sync loop forever.
    /// </summary>
    public static AsyncPolicy CreateDrivePolicy(ILogger logger, RetryPolicyConfig? config, TimeSpan attemptTimeout)
        => CreateRetry(logger, config).WrapAsync(
            Policy.TimeoutAsync(attemptTimeout, Polly.Timeout.TimeoutStrategy.Pessimistic));

    private static AsyncRetryPolicy CreateRetry(ILogger logger, RetryPolicyConfig? config)
    {
        var retryCount = config?.RetryCount ?? 5;
        var baseDelayMs = config?.BaseDelayMs ?? 1000;
        var maxDelayMs = config?.MaxDelayMs ?? 30000;

        return Policy
            // 429, 5xx and Drive's 403 rate-limit errors (userRateLimitExceeded etc.)
            .Handle<Google.GoogleApiException>(ex => DriveErrors.IsTransient(ex))
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>(ex => ex.CancellationToken == default)
            .Or<Polly.Timeout.TimeoutRejectedException>()
            .Or<System.Net.Sockets.SocketException>()
            .WaitAndRetryAsync(
                retryCount: retryCount,
                sleepDurationProvider: (attempt, _) =>
                {
                    var delay = TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt - 1));
                    var maxDelay = TimeSpan.FromMilliseconds(maxDelayMs);
                    if (delay > maxDelay) delay = maxDelay;
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    return delay + jitter;
                },
                onRetry: (ex, delay, attempt, _) =>
                    logger.LogWarning(
                        "Drive API retry {Attempt}/{Max} after {Delay:0.0}s: {Message}",
                        attempt, retryCount, delay.TotalSeconds, ex.Message));
    }

    /// <summary>
    /// Short retry policy for quick operations that should not block long.
    /// Defaults: 3 retries with 500ms fixed delay.
    /// </summary>
    public static AsyncRetryPolicy CreateQuickPolicy(ILogger logger, RetryPolicyConfig? config = null)
    {
        var retryCount = config?.RetryCount ?? 3;
        var baseDelayMs = config?.BaseDelayMs ?? 500;

        return Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: retryCount,
                sleepDurationProvider: _ => TimeSpan.FromMilliseconds(baseDelayMs),
                onRetry: (ex, _, attempt, _) =>
                    logger.LogDebug("Quick retry {Attempt}/{Max}: {Message}", attempt, retryCount, ex.Message));
    }
}
