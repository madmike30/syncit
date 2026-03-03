using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Resolves file sync conflicts based on the configured ConflictResolution policy.
///
/// A true conflict requires:
///   1. File changed on LOCAL side since baseline (hash differs from baseline local record)
///   2. File changed on REMOTE side since baseline (revision ID or hash differs)
///   3. Both changed to DIFFERENT content (same content = convergent edit, no conflict)
///
/// Special case: mtime within 2 seconds is treated as a potential conflict regardless.
/// </summary>
public sealed class ConflictResolver : IConflictResolver
{
    private readonly ILogger<ConflictResolver> _logger;

    // Two timestamps within this window are treated as "same time" (clock skew tolerance)
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    public ConflictResolver(ILogger<ConflictResolver> logger)
    {
        _logger = logger;
    }

    public ConflictResolutionAction Resolve(ConflictPolicyOptions policy, ConflictContext conflict)
    {
        var local   = conflict.LocalRecord;
        var remote  = conflict.RemoteRecord;

        // If hashes match (and both have hashes), content is identical — no action needed
        if (local.ContentHash is not null &&
            remote.ContentHash is not null &&
            local.ContentHash.Equals(remote.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Conflict resolved: content identical for '{Path}'", conflict.RelativePath);
            return ConflictResolutionAction.NoConflict();
        }

        var outcome = policy.Resolution switch
        {
            ConflictResolution.PreferLocal  => ResolvePreferLocal(conflict),
            ConflictResolution.PreferRemote => ResolvePreferRemote(conflict),
            ConflictResolution.KeepBoth     => ResolveKeepBoth(policy, conflict),
            _                               => ResolveLastWriteWins(conflict),
        };

        _logger.LogInformation(
            "Conflict on '{Path}': {Outcome} ({Reason}) | local={LocalTime} remote={RemoteTime}",
            conflict.RelativePath,
            outcome.Outcome,
            outcome.ReasonDescription,
            local.ModifiedUtc.ToString("O"),
            remote.ModifiedUtc.ToString("O"));

        return outcome;
    }

    // -------------------------------------------------------------------------
    // Resolution strategies
    // -------------------------------------------------------------------------

    private static ConflictResolutionAction ResolveLastWriteWins(ConflictContext c)
    {
        var timeDiff = c.LocalRecord.ModifiedUtc - c.RemoteRecord.ModifiedUtc;

        if (Math.Abs(timeDiff.TotalSeconds) <= MtimeTolerance.TotalSeconds)
        {
            // Timestamps are too close to determine a winner — prefer local (safer)
            return new ConflictResolutionAction
            {
                Outcome           = ConflictOutcome.UseLocal,
                ReasonDescription = "LastWriteWins: timestamps within tolerance — preferring local",
            };
        }

        return timeDiff > TimeSpan.Zero
            ? new ConflictResolutionAction
            {
                Outcome           = ConflictOutcome.UseLocal,
                ReasonDescription = $"LastWriteWins: local is newer by {timeDiff.TotalSeconds:0}s",
            }
            : new ConflictResolutionAction
            {
                Outcome           = ConflictOutcome.UseRemote,
                ReasonDescription = $"LastWriteWins: remote is newer by {(-timeDiff).TotalSeconds:0}s",
            };
    }

    private static ConflictResolutionAction ResolvePreferLocal(ConflictContext _) =>
        new() { Outcome = ConflictOutcome.UseLocal, ReasonDescription = "PreferLocal policy" };

    private static ConflictResolutionAction ResolvePreferRemote(ConflictContext _) =>
        new() { Outcome = ConflictOutcome.UseRemote, ReasonDescription = "PreferRemote policy" };

    private static ConflictResolutionAction ResolveKeepBoth(ConflictPolicyOptions policy, ConflictContext c)
    {
        var suffix = policy.KeepBothSuffix
            .Replace("{date}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

        var remoteConflictPath = PathHelper.InsertSuffix(c.RelativePath, suffix);

        return new ConflictResolutionAction
        {
            Outcome              = ConflictOutcome.KeepBoth,
            KeepBothRemotePath   = remoteConflictPath,
            ReasonDescription    = $"KeepBoth: remote copy saved as '{remoteConflictPath}'",
        };
    }
}
