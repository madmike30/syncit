using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>Pure logic for the multi-account model: migration from single-account configs,
/// pair→account resolution, and adding/removing accounts. No I/O except the token-exists probe.</summary>
public static class AccountRegistry
{
    /// <summary>ID given to the account that existed before 1.3 (its token file is left exactly where it was).</summary>
    public const string LegacyAccountId = "default";

    public static string NewTokenPath(string accountId) => $@"%APPDATA%\SyncIt\accounts\{accountId}.bin";

    /// <summary>
    /// Migrates a pre-1.3 config in place: the existing token file becomes account "default" and every
    /// pair without an account is bound to it. Nothing is moved or re-authorized, and pair IDs (the
    /// state-database keys) are unchanged, so no re-sign-in and no re-sync. Returns true if it changed anything.
    /// </summary>
    public static bool Migrate(SyncConfig config, Func<string, bool> tokenExists)
    {
        var changed = false;
        if (config.Accounts.Count == 0 && tokenExists(config.TokenStorePath))
        {
            config.Accounts.Add(new GoogleAccount { Id = LegacyAccountId, TokenStorePath = config.TokenStorePath });
            changed = true;
        }

        var fallback = config.Accounts.FirstOrDefault()?.Id;
        if (fallback is null) return changed;

        foreach (var pair in config.SyncPairs)
        {
            if (string.IsNullOrEmpty(pair.AccountId) || config.Accounts.All(a => a.Id != pair.AccountId))
            {
                pair.AccountId = fallback;
                changed = true;
            }
        }
        return changed;
    }

    public static GoogleAccount? ForPair(SyncConfig config, SyncPair pair) =>
        config.Accounts.FirstOrDefault(a => a.Id == pair.AccountId) ?? config.Accounts.FirstOrDefault();

    public static GoogleAccount Add(SyncConfig config, string email)
    {
        var existing = config.Accounts.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var account = new GoogleAccount { Email = email };
        account.TokenStorePath = NewTokenPath(account.Id);
        config.Accounts.Add(account);
        return account;
    }

    /// <summary>Removes the account and the pairs bound to it (local and Drive files are not touched).
    /// Returns the removed pairs so the caller can clear their sync state.</summary>
    public static IReadOnlyList<SyncPair> Remove(SyncConfig config, string accountId)
    {
        var removedPairs = config.SyncPairs.Where(p => p.AccountId == accountId).ToList();
        config.SyncPairs.RemoveAll(p => p.AccountId == accountId);
        config.Accounts.RemoveAll(a => a.Id == accountId);
        return removedPairs;
    }

    /// <summary>IDs of pairs present in both configs whose account, shared drive or remote folder changed
    /// (their stored Drive file IDs are no longer valid).</summary>
    public static IReadOnlyList<string> PairsWithChangedRemote(SyncConfig oldConfig, SyncConfig newConfig)
    {
        var result = new List<string>();
        foreach (var n in newConfig.SyncPairs)
        {
            var o = oldConfig.SyncPairs.FirstOrDefault(p => p.Id == n.Id);
            if (o is null) continue;
            var oldAccount = ForPair(oldConfig, o)?.Id;
            var newAccount = ForPair(newConfig, n)?.Id;
            if (!string.Equals(o.RemoteFolderId, n.RemoteFolderId, StringComparison.Ordinal) ||
                !string.Equals(o.SharedDriveId, n.SharedDriveId, StringComparison.Ordinal) ||
                !string.Equals(oldAccount, newAccount, StringComparison.Ordinal))
                result.Add(n.Id);
        }
        return result;
    }

    /// <summary>Points a pair at an account's My Drive (drive = null) or at the root of one of its shared drives.</summary>
    public static void ApplyLocation(SyncPair pair, string accountId, SharedDriveInfo? drive)
    {
        pair.AccountId = accountId;
        pair.SharedDriveId = drive?.Id;
        pair.RemoteFolderId = drive?.Id ?? "root";
        pair.RemoteFolderName = drive is null ? "My Drive" : "Shared drive: " + drive.Name;
        pair.SelectedSubfolders.Clear();
    }

    /// <summary>Pre-1.3 builds moved every non-root pair to the Drive root. Shared-drive pairs must keep their remote.</summary>
    public static bool NeedsLegacyRootMigration(SyncPair pair) =>
        pair.SharedDriveId is null && pair.RemoteFolderId != "root" && !string.IsNullOrEmpty(pair.RemoteFolderId);
}
