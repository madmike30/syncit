using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.Core.Helpers;

/// <summary>Stable colour + initial badge per Google account, so pairs/activity show which account they use.</summary>
public static class AccountColors
{
    /// <summary>Windows 11-friendly accent palette (readable with white text).</summary>
    public static readonly string[] Palette =
    {
        "#0F6CBD", "#C239B3", "#107C10", "#CA5010", "#8764B8", "#038387", "#D13438", "#4F6BED",
    };

    /// <summary>Colour by the account's position in the config (first account = blue), so colours never collide for ≤ 8 accounts.</summary>
    public static string For(SyncConfig config, string? accountId)
    {
        var idx = config.Accounts.FindIndex(a => a.Id == accountId);
        if (idx < 0) return "#707070";
        return Palette[idx % Palette.Length];
    }

    /// <summary>One-letter badge: first letter of the e-mail, upper-case.</summary>
    public static string Initial(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "?" : char.ToUpperInvariant(email.Trim()[0]).ToString();

    public static string Label(SyncConfig config, SyncPair pair)
    {
        var a = AccountRegistry.ForPair(config, pair);
        return a is null ? "No account" : string.IsNullOrEmpty(a.Email) ? a.Id : a.Email;
    }

    /// <summary>"My Drive", "My Drive / Folder" or "Shared drive: X".</summary>
    public static string RemoteLocation(SyncPair pair)
    {
        var name = string.IsNullOrWhiteSpace(pair.RemoteFolderName) ? "My Drive" : pair.RemoteFolderName.Trim();
        if (name.StartsWith("Shared drive", StringComparison.OrdinalIgnoreCase)) return name;
        if (pair.SharedDriveId is not null)
            return name.StartsWith("Shared drive", StringComparison.OrdinalIgnoreCase) ? name : "Shared drive: " + name;
        if (pair.RemoteFolderId == "root" || name.Equals("My Drive", StringComparison.OrdinalIgnoreCase)) return "My Drive";
        return name.StartsWith("My Drive", StringComparison.OrdinalIgnoreCase) ? name : "My Drive / " + name;
    }
}

/// <summary>Active hours ("only sync 08:00-23:00").</summary>
public static class SyncSchedule
{
    public static bool IsActive(SyncConfig config, DateTime localNow) =>
        IsActive(config.ActiveHoursEnabled, config.ActiveHoursStart, config.ActiveHoursEnd, localNow);

    public static bool IsActive(bool enabled, int startHour, int endHour, DateTime localNow)
    {
        if (!enabled) return true;
        startHour = Math.Clamp(startHour, 0, 23);
        endHour = Math.Clamp(endHour, 0, 24) % 24;
        if (startHour == endHour) return true; // 24 h window
        var h = localNow.Hour;
        return startHour < endHour ? h >= startHour && h < endHour : h >= startHour || h < endHour;
    }

    public static string Describe(SyncConfig c) =>
        c.ActiveHoursEnabled ? $"{c.ActiveHoursStart:00}:00-{c.ActiveHoursEnd:00}:00" : "Always";
}

/// <summary>What a pair's mode allows. Backup-only never changes anything on the PC.</summary>
public static class SyncModePolicy
{
    public static bool IsBackupOnly(SyncPair pair) => pair.Mode == SyncMode.BackupOnly;
    public static bool AllowsDownload(SyncPair pair) => !IsBackupOnly(pair);
    public static bool AllowsLocalDeleteFromRemote(SyncPair pair) => !IsBackupOnly(pair);
    public static bool PropagatesLocalDeletes(SyncPair pair) => !IsBackupOnly(pair) || !pair.BackupKeepDeletedOnDrive;

    /// <summary>In backup mode the PC copy always wins a conflict (Drive is overwritten, local untouched).</summary>
    public static bool ForceLocalWins(SyncPair pair) => IsBackupOnly(pair);

    public static string Describe(SyncPair pair) => IsBackupOnly(pair)
        ? (pair.BackupKeepDeletedOnDrive ? "Backup only (PC → Drive, keeps deleted files on Drive)" : "Backup only (PC → Drive)")
        : "Two-way";
}

/// <summary>Which activity becomes a Windows notification, and its text.</summary>
public static class ActivityNotifier
{
    public static bool ShouldNotify(ActivityRecord r, bool conflicts, bool deletions) => r.Type switch
    {
        ActivityType.ConflictResolved => conflicts,
        ActivityType.Deleted or ActivityType.FolderDeleted => deletions,
        _ => false,
    };

    /// <summary>One notification for a burst of records (avoids a toast storm).</summary>
    public static (string Title, string Text)? Summarize(IReadOnlyList<ActivityRecord> batch, Func<string, string?> accountForPair)
    {
        if (batch.Count == 0) return null;
        var conflicts = batch.Where(r => r.Type == ActivityType.ConflictResolved).ToList();
        var deletes = batch.Where(r => r.Type is ActivityType.Deleted or ActivityType.FolderDeleted).ToList();
        var accounts = batch.Select(r => accountForPair(r.SyncPairId)).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
        var who = accounts.Count == 1 ? $" ({accounts[0]})" : "";
        string Names(List<ActivityRecord> l) => l.Count == 1 ? Path.GetFileName(l[0].RelativePath)
            : $"{Path.GetFileName(l[0].RelativePath)} and {l.Count - 1} more";
        if (conflicts.Count > 0 && deletes.Count == 0)
            return ($"SyncIt: {conflicts.Count} conflict{(conflicts.Count == 1 ? "" : "s")} resolved{who}", Names(conflicts));
        if (deletes.Count > 0 && conflicts.Count == 0)
            return ($"SyncIt: {deletes.Count} item{(deletes.Count == 1 ? "" : "s")} deleted{who}", Names(deletes) + " (recoverable from the SyncIt trash / Drive trash)");
        return ($"SyncIt: {conflicts.Count} conflict(s), {deletes.Count} deletion(s){who}", Names(batch.ToList()));
    }
}
