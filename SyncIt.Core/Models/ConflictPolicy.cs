namespace SyncIt.Core.Models;

public enum ConflictResolution
{
    /// <summary>The file with the newer ModifiedUtc wins and overwrites the other.</summary>
    LastWriteWins,

    /// <summary>
    /// Keep local file as-is; download remote to a renamed path.
    /// e.g. "report (SyncIt conflict 2026-02-23).docx"
    /// </summary>
    KeepBoth,

    /// <summary>Local version always wins. Remote changes are discarded.</summary>
    PreferLocal,

    /// <summary>Remote version always wins. Local changes are discarded.</summary>
    PreferRemote,
}

public class ConflictPolicyOptions
{
    public ConflictResolution Resolution { get; set; } = ConflictResolution.LastWriteWins;

    /// <summary>
    /// Suffix template for KeepBoth mode.
    /// Supports {date} (yyyy-MM-dd) substitution.
    /// </summary>
    public string KeepBothSuffix { get; set; } = "(SyncIt conflict {date})";
}
