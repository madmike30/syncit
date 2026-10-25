using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>Pure helpers behind the Settings window (unit-tested).</summary>
public static class PairSettingsEditor
{
    /// <summary>One pattern per line (or comma separated); blanks and duplicates removed.</summary>
    public static List<string> ParsePatterns(string? text) =>
        (text ?? "")
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string FormatPatterns(IEnumerable<string> patterns) => string.Join(Environment.NewLine, patterns);

    /// <summary>Applies the per-pair options edited in Settings. Returns true if anything changed.</summary>
    public static bool Apply(SyncPair pair, ConflictResolution resolution, string excludesText, bool paused)
    {
        var excludes = ParsePatterns(excludesText);
        var changed = pair.ConflictPolicy.Resolution != resolution || pair.Paused != paused ||
                      !pair.ExcludeGlobs.SequenceEqual(excludes, StringComparer.Ordinal);
        pair.ConflictPolicy.Resolution = resolution;
        pair.ExcludeGlobs = excludes;
        pair.Paused = paused;
        return changed;
    }

    /// <summary>KB/s limit: 0 = unlimited, otherwise clamped to 16 KB/s – 1 GB/s.</summary>
    public static int ClampBandwidth(decimal kbps) => kbps <= 0 ? 0 : (int)Math.Clamp(kbps, 16, 1_048_576);

    /// <summary>Sets the sync direction; returns true when something changed.</summary>
    public static bool ApplyMode(SyncPair pair, SyncMode mode, bool keepDeletedOnDrive)
    {
        var changed = pair.Mode != mode || pair.BackupKeepDeletedOnDrive != keepDeletedOnDrive;
        pair.Mode = mode;
        pair.BackupKeepDeletedOnDrive = keepDeletedOnDrive;
        return changed;
    }

    public static string DescribePair(SyncPair pair, SyncConfig config)
    {
        var account = AccountRegistry.ForPair(config, pair);
        var who = account is null ? "no account" : string.IsNullOrEmpty(account.Email) ? account.Id : account.Email;
        var state = !pair.Enabled ? " (off)" : pair.Paused ? " (paused)" : "";
        var mode = pair.Mode == SyncMode.BackupOnly ? " [backup]" : "";
        var accPaused = account?.Paused == true ? " (account paused)" : "";
        return $"{who} · {Helpers.AccountColors.RemoteLocation(pair)}  ↔  {pair.LocalPath}{mode}{state}{accPaused}";
    }
}
