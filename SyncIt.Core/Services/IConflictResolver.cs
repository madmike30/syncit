using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

public interface IConflictResolver
{
    /// <summary>
    /// Resolve a conflict where both local and remote versions of a file have changed
    /// since the last known baseline.
    /// Returns a ConflictResolutionAction describing what to do.
    /// </summary>
    ConflictResolutionAction Resolve(ConflictPolicyOptions policy, ConflictContext conflict);
}

/// <summary>All the information needed to resolve a conflict.</summary>
public class ConflictContext
{
    public FileRecord LocalRecord   { get; init; } = null!;
    public FileRecord RemoteRecord  { get; init; } = null!;
    public FileRecord? BaselineRecord { get; init; }  // null = no baseline (initial sync)
    public string RelativePath      { get; init; } = string.Empty;
}

public enum ConflictOutcome
{
    UseLocal,           // Upload local version, overwrite remote
    UseRemote,          // Download remote version, overwrite local
    KeepBoth,           // Keep local, download remote to a renamed path
    NoConflict,         // Content is identical — no action needed
}

public class ConflictResolutionAction
{
    public ConflictOutcome Outcome        { get; init; }
    public string? KeepBothRemotePath     { get; init; }  // only set when Outcome=KeepBoth
    public string  ReasonDescription      { get; init; } = string.Empty;

    public static ConflictResolutionAction NoConflict() =>
        new() { Outcome = ConflictOutcome.NoConflict, ReasonDescription = "Content identical" };
}
