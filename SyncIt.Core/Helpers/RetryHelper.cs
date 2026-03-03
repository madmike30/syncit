using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Polly-based retry policies for Google Drive API calls.
/// Handles transient failures: 429 (rate limit), 503 (unavailable), network errors.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Standard retry policy for Drive API operations.
    /// 5 retries with exponential backoff + jitter: ~1s, 2s, 4s, 8s, 16s.
    /// </summary>
    public static AsyncRetryPolicy CreateDrivePolicy(ILogger logger) =>
        Policy
            .Handle<Google.GoogleApiException>(ex =>
                ex.HttpStatusCode is
                    System.Net.HttpStatusCode.TooManyRequests or
                    System.Net.HttpStatusCode.ServiceUnavailable or
                    System.Net.HttpStatusCode.InternalServerError)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>(ex => ex.CancellationToken == default)
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: (attempt, _) =>
                {
                    var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    var jitter    = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    return baseDelay + jitter;
                },
                onRetry: (ex, delay, attempt, _) =>
                    logger.LogWarning(
                        "Drive API retry {Attempt}/5 after {Delay:0.0}s: {Message}",
                        attempt, delay.TotalSeconds, ex.Message));

    /// <summary>
    /// Short retry policy for quick operations that should not block long.
    /// 3 retries with 500ms fixed delay.
    /// </summary>
    public static AsyncRetryPolicy CreateQuickPolicy(ILogger logger) =>
        Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: _ => TimeSpan.FromMilliseconds(500),
                onRetry: (ex, _, attempt, _) =>
                    logger.LogDebug("Quick retry {Attempt}/3: {Message}", attempt, ex.Message));
}
