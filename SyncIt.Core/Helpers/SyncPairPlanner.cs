using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>Decides which configured pairs get an engine.</summary>
public static class SyncPairPlanner
{
    /// <summary>
    /// Enabled pairs, at most one per pair ID and one per local folder.
    /// Two engines on the same folder race each other and can create duplicate files on Drive.
    /// </summary>
    public static List<SyncPair> EnabledUnique(IEnumerable<SyncPair> pairs)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SyncPair>();
        foreach (var pair in pairs)
        {
            if (!pair.Enabled) continue;
            var folder = NormalizeFolder(pair.LocalPath);
            if (!ids.Add(pair.Id ?? string.Empty)) continue;
            if (!folders.Add(folder)) continue;
            result.Add(pair);
        }
        return result;
    }

    public static string NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return PathHelper.Expand(path.Trim()).Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>A new, unused pair ID such as "pair002".</summary>
    public static string NextPairId(IEnumerable<SyncPair> pairs)
    {
        var used = new HashSet<string>(pairs.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var id = $"pair{i:000}";
            if (!used.Contains(id)) return id;
        }
    }
}
