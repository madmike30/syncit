using System.Text.Json;
using System.Text.Json.Nodes;
using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Removes everything that ties this PC to a Google account: the saved sign-in token,
/// the sync state databases, and the configured sync pairs.
/// It never touches the user's synced folders or anything on Google Drive.
/// </summary>
public static class AccountReset
{
    public static IReadOnlyList<string> RemoveLocalAccount(string configPath, SyncConfig config)
    {
        var removed = new List<string>();

        void Delete(string file)
        {
            try
            {
                if (File.Exists(file)) { File.Delete(file); removed.Add(file); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Delete(PathHelper.Expand(config.TokenStorePath));
        foreach (var account in config.Accounts) Delete(PathHelper.Expand(account.TokenStorePath));
        foreach (var db in new[] { config.StateDbPath, config.DeleteJournalPath, config.ActivityLogPath })
        {
            var path = PathHelper.Expand(db);
            foreach (var ext in new[] { "", "-shm", "-wal" }) Delete(path + ext);
        }

        // Drop the pairs but keep every other setting (poll interval, bandwidth, ...).
        if (File.Exists(configPath))
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? new JsonObject();
            foreach (var key in root.Select(kv => kv.Key).Where(k => k.Equals("SyncPairs", StringComparison.OrdinalIgnoreCase) || k.Equals("Accounts", StringComparison.OrdinalIgnoreCase)).ToList())
                root.Remove(key);
            root["SyncPairs"] = new JsonArray();
            root["Accounts"] = new JsonArray();
            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        config.SyncPairs.Clear();
        config.Accounts.Clear();
        return removed;
    }
}
