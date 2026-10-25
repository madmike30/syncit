using System.Net.Sockets;
using Polly.Timeout;

namespace SyncIt.Core.Helpers;

/// <summary>Decides whether a failure means "Google Drive is unreachable" (offline, DNS, timeout).</summary>
public static class NetworkErrors
{
    public static bool IsNetworkError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException:
                case HttpRequestException { StatusCode: null }:
                case TimeoutRejectedException:
                case TimeoutException:
                case IOException when e.InnerException is SocketException:
                    return true;
                case TaskCanceledException tce when tce.CancellationToken == default:
                    return true; // HttpClient timeout
            }
        }
        return false;
    }
}

/// <summary>Delay before the next sync cycle after consecutive failures.</summary>
public static class RecoveryBackoff
{
    /// <summary>
    /// 0 failures → the normal poll interval. Then poll, 2x, 4x … capped at <paramref name="maxSeconds"/>
    /// (but never below the poll interval). Network-change and resume events cut the wait short.
    /// </summary>
    public static TimeSpan Next(int consecutiveFailures, int pollSeconds, int maxSeconds = 300)
    {
        pollSeconds = Math.Max(1, pollSeconds);
        if (consecutiveFailures <= 0) return TimeSpan.FromSeconds(pollSeconds);
        var cap = Math.Max(pollSeconds, maxSeconds);
        var sec = pollSeconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(sec, cap));
    }
}
