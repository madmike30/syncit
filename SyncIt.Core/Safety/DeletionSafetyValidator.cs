using Microsoft.Extensions.Logging;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.Core.Safety;

/// <summary>
/// Layer 2 deletion safety: checks that the number of pending deletions in a single sync
/// cycle doesn't exceed the configured threshold.
///
/// If the threshold is exceeded, ALL deletions for that cycle are FROZEN and the user
/// must explicitly approve them via the tray app before any are executed.
///
/// This protects against:
///  - Misconfigured localPath (empty folder → would delete everything from Drive)
///  - Bulk accidental local deletion (e.g. rm -rf in wrong directory)
///  - Drive-side mass deletion propagating locally
/// </summary>
public sealed class DeletionSafetyValidator
{
    private readonly SyncConfig _config;
    private readonly IFileStateStore _store;
    private readonly DeletionJournal _journal;
    private readonly ILogger<DeletionSafetyValidator> _logger;

    public DeletionSafetyValidator(
        SyncConfig config,
        IFileStateStore store,
        DeletionJournal journal,
        ILogger<DeletionSafetyValidator> logger)
    {
        _config  = config;
        _store   = store;
        _journal = journal;
        _logger  = logger;
    }

    /// <summary>
    /// Validate pending deletions for a sync cycle.
    /// Returns ValidationResult indicating whether to proceed, freeze, or abort.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        SyncPair pair,
        IReadOnlyList<DeletionRecord> pendingDeletions,
        CancellationToken ct = default)
    {
        if (pendingDeletions.Count == 0)
            return ValidationResult.Proceed(pendingDeletions);

        // Layer 1: verify each deletion has a baseline record (exists in our DB)
        var withBaseline = new List<DeletionRecord>();
        var withoutBaseline = new List<DeletionRecord>();

        foreach (var del in pendingDeletions)
        {
            var baseline = await _store.GetRecordAsync(pair.Id, del.Side, del.RelativePath);
            if (baseline is not null && !baseline.IsDeleted)
                withBaseline.Add(del);
            else
            {
                _logger.LogWarning(
                    "Deletion of '{Path}' rejected: no baseline record found. " +
                    "File was never tracked — ignoring to prevent phantom delete.",
                    del.RelativePath);
                withoutBaseline.Add(del);
            }
        }

        if (withBaseline.Count == 0)
            return ValidationResult.Proceed(new List<DeletionRecord>());

        // Layer 2: threshold check — count only active (Pending) deletions, NOT frozen ones.
        // Frozen deletions are already blocked and awaiting user action — they shouldn't
        // prevent new small-batch deletions from proceeding normally.
        var alreadyPending = await _journal.CountActivePendingAsync(pair.Id, ct);
        var totalPending = alreadyPending + withBaseline.Count;

        if (totalPending > _config.MaxDeletionsPerCycle)
        {
            _logger.LogWarning(
                "DELETION FREEZE: {Total} total pending deletions ({Existing} existing + {New} new) " +
                "exceeds threshold={Max}. Freezing for pair '{PairId}' until user approves.",
                totalPending, alreadyPending, withBaseline.Count, _config.MaxDeletionsPerCycle, pair.Id);

            return ValidationResult.Freeze(withBaseline,
                $"{totalPending} files would be deleted (limit: {_config.MaxDeletionsPerCycle}).");
        }

        _logger.LogDebug("Deletion validation passed: {Count} deletions approved for grace period.", withBaseline.Count);
        return ValidationResult.Proceed(withBaseline);
    }

    /// <summary>
    /// Startup safety check: validates the local folder is not unexpectedly empty.
    /// Should be called before any sync begins.
    /// </summary>
    public async Task<StartupCheckResult> CheckLocalFolderAsync(SyncPair pair, CancellationToken ct = default)
    {
        if (!Directory.Exists(pair.LocalPath))
        {
            _logger.LogError("Local path does not exist: {Path}", pair.LocalPath);
            return StartupCheckResult.Fail($"Local folder does not exist: {pair.LocalPath}");
        }

        var baselineCount = await _store.CountRecordsAsync(pair.Id, SyncSide.Local);
        if (baselineCount == 0)
        {
            // No baseline yet — this is a first run, which is fine
            return StartupCheckResult.Ok();
        }

        // We have a baseline. Check if there are any local files (including subdirectories)
        var localFileCount = Directory.EnumerateFiles(pair.LocalPath, "*", SearchOption.AllDirectories).Take(1).Any() ? 1 : 0;

        if (localFileCount == 0)
        {
            // The folder appears to be empty but we have N baseline records.
            // This is very suspicious — could be wrong folder configured.
            _logger.LogError(
                "SAFETY HALT: Local folder '{Path}' appears empty but baseline has {Count} records. " +
                "Possible misconfigured sync pair. Halting to prevent data loss.",
                pair.LocalPath, baselineCount);

            return StartupCheckResult.Fail(
                $"Local folder '{pair.LocalPath}' is empty but {baselineCount} files were expected. " +
                "Check your sync pair configuration before proceeding.");
        }

        return StartupCheckResult.Ok();
    }
}

public class ValidationResult
{
    public bool     ShouldFreeze  { get; private init; }
    public string?  FreezeReason  { get; private init; }
    public IReadOnlyList<DeletionRecord> ApprovedDeletions { get; private init; } = Array.Empty<DeletionRecord>();

    public static ValidationResult Proceed(IReadOnlyList<DeletionRecord> deletions) =>
        new() { ShouldFreeze = false, ApprovedDeletions = deletions };

    public static ValidationResult Freeze(IReadOnlyList<DeletionRecord> deletions, string reason) =>
        new() { ShouldFreeze = true, FreezeReason = reason, ApprovedDeletions = deletions };
}

public class StartupCheckResult
{
    public bool   IsOk          { get; private init; }
    public string? ErrorMessage  { get; private init; }

    public static StartupCheckResult Ok()                   => new() { IsOk = true };
    public static StartupCheckResult Fail(string message)  => new() { IsOk = false, ErrorMessage = message };
}
