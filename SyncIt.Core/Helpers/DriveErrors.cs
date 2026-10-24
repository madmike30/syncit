using System.Net;
using Google;

namespace SyncIt.Core.Helpers;

/// <summary>Classifies Google Drive API errors.</summary>
public static class DriveErrors
{
    private static readonly HashSet<string> RateLimitReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "rateLimitExceeded", "userRateLimitExceeded", "sharingRateLimitExceeded", "dailyLimitExceeded",
    };

    /// <summary>
    /// True for errors worth retrying with backoff: 429, 5xx, and Drive's 403 rate-limit errors
    /// (Drive reports most per-user rate limits as 403, not 429).
    /// </summary>
    public static bool IsTransient(Exception ex) => ex is GoogleApiException g && (
        g.HttpStatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || (g.HttpStatusCode == HttpStatusCode.Forbidden && IsRateLimitReason(g)));

    public static bool IsRateLimitReason(GoogleApiException g) =>
        g.Error?.Errors?.Any(e => e.Reason is not null && RateLimitReasons.Contains(e.Reason)) == true;

    /// <summary>
    /// True when a saved Changes API page token is no longer valid (410 Gone, or 400/404 with
    /// an invalid page token). SyncIt then takes a fresh token and does a full rescan.
    /// </summary>
    public static bool IsExpiredPageToken(Exception ex) => ex is GoogleApiException g && (
        g.HttpStatusCode == HttpStatusCode.Gone
        || (g.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            && (g.Error?.Errors?.Any(e => string.Equals(e.Location, "pageToken", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Reason, "invalidPageToken", StringComparison.OrdinalIgnoreCase)) == true
                || g.Message.Contains("pageToken", StringComparison.OrdinalIgnoreCase))));
}
