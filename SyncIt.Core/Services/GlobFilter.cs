using DotNet.Globbing;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Applies include/exclude glob patterns to file paths using DotNet.Glob.
///
/// Rules (applied in order):
/// 1. If path matches any ExcludeGlob → excluded.
/// 2. If IncludeGlobs is empty → included (include everything not excluded).
/// 3. If path matches any IncludeGlob → included.
/// 4. Otherwise → excluded.
/// </summary>
public sealed class GlobFilter : IGlobFilter
{
    // Cache compiled globs per pair to avoid recompiling on every file
    private readonly Dictionary<string, (List<Glob> Excludes, List<Glob> Includes)> _cache = new();
    private readonly object _lock = new();

    public bool IsIncluded(string relativePath, SyncPair pair) => !IsExcluded(relativePath, pair);

    /// <summary>
    /// Temporary and lock files that are never synced, whatever the pair's own patterns say:
    /// Office owner files, browser partial downloads, LibreOffice and editor lock/swap files.
    /// Syncing them creates junk on Drive and fails while the app holds them open.
    /// </summary>
    public static bool IsTemporaryFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.StartsWith("~$", StringComparison.Ordinal)) return true;            // Office owner file
        if (fileName.StartsWith(".~lock.", StringComparison.Ordinal) && fileName.EndsWith('#')) return true; // LibreOffice
        if (fileName.StartsWith("~", StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)  // Chrome/Edge
            || ext.Equals(".partial", StringComparison.OrdinalIgnoreCase)     // Edge (old)
            || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)        // Firefox
            || ext.Equals(".download", StringComparison.OrdinalIgnoreCase)    // Safari
            || ext.Equals(".swp", StringComparison.OrdinalIgnoreCase)         // vim
            || fileName.Contains(".syncit-tmp-", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsExcluded(string relativePath, SyncPair pair)
    {
        // Normalize path to forward slashes (glob patterns use /)
        var path = relativePath.Replace('\\', '/');

        // Rule 0: built-in temporary/lock files
        var slash = path.LastIndexOf('/');
        if (IsTemporaryFileName(slash >= 0 ? path[(slash + 1)..] : path)) return true;

        var (excludes, includes) = GetCompiledGlobs(pair);

        // Rule 1: explicit exclude wins
        foreach (var g in excludes)
            if (g.IsMatch(path)) return true;

        // Rule 1.5: Selective subfolder filter.
        // If SelectedSubfolders is non-empty, only allow items that are inside
        // (or equal to) one of the explicitly selected subfolders.
        if (pair.SelectedSubfolders.Count > 0)
        {
            bool inSelectedSubfolder = pair.SelectedSubfolders.Any(sf =>
            {
                var prefix = sf.Replace('\\', '/').TrimEnd('/');
                return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || prefix.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
            });
            if (!inSelectedSubfolder) return true;
        }

        // Rule 2: empty include list = include all
        if (includes.Count == 0) return false;

        // Rule 3: must match at least one include glob
        foreach (var g in includes)
            if (g.IsMatch(path)) return false;

        // Rule 4: not in include list
        return true;
    }

    private (List<Glob> Excludes, List<Glob> Includes) GetCompiledGlobs(SyncPair pair)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pair.Id, out var cached))
                return cached;

            var opts = new GlobOptions { Evaluation = { CaseInsensitive = true } };
            var excludes = pair.ExcludeGlobs.Select(p => Glob.Parse(p, opts)).ToList();
            var includes = pair.IncludeGlobs.Select(p => Glob.Parse(p, opts)).ToList();
            _cache[pair.Id] = (excludes, includes);
            return (excludes, includes);
        }
    }

    /// <summary>Invalidate the cached globs for a pair (call when config changes).</summary>
    public void InvalidateCache(string pairId)
    {
        lock (_lock) { _cache.Remove(pairId); }
    }
}
