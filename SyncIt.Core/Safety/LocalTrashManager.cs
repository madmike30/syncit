using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Safety;

/// <summary>
/// Moves locally-deleted files to a trash folder instead of permanently deleting them.
/// This is the local equivalent of Drive's Trash.
///
/// Structure:
///   .syncit-trash\
///     {pairId}\
///       2026-02-23T14-30-00Z\
///         docs\report.docx
///         images\photo.jpg
///
/// Files in .syncit-trash are not synced (excluded by default glob pattern).
/// Auto-pruning removes entries older than RetentionDays (default 30).
/// </summary>
public sealed class LocalTrashManager
{
    public const int RetentionDays = 30;

    private readonly string _trashRoot;
    private readonly ILogger<LocalTrashManager> _logger;

    public LocalTrashManager(string trashRootPath, ILogger<LocalTrashManager> logger)
    {
        _trashRoot = PathHelper.Expand(trashRootPath);
        _logger    = logger;
    }

    /// <summary>
    /// Move a local file to the trash folder. Returns the trash destination path.
    /// </summary>
    public string MoveToTrash(string pairId, string localAbsolutePath, string relativePath)
    {
        var timestamp   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var trashFolder = Path.Combine(_trashRoot, pairId, timestamp);
        var trashPath   = Path.Combine(trashFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        PathHelper.EnsureDirectory(Path.GetDirectoryName(trashPath)!);

        if (!File.Exists(localAbsolutePath))
        {
            _logger.LogWarning("LocalTrashManager: file no longer exists at {Path} — nothing to trash.", localAbsolutePath);
            return trashPath;
        }

        File.Move(localAbsolutePath, trashPath, overwrite: false);
        _logger.LogInformation("Moved '{Source}' to trash at '{Dest}'", localAbsolutePath, trashPath);
        return trashPath;
    }

    /// <summary>
    /// Prune trash entries older than RetentionDays. Call periodically (e.g. once daily).
    /// </summary>
    public void PruneOldEntries()
    {
        if (!Directory.Exists(_trashRoot)) return;

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        foreach (var pairDir in Directory.EnumerateDirectories(_trashRoot))
        {
            foreach (var timestampDir in Directory.EnumerateDirectories(pairDir))
            {
                var dirName = Path.GetFileName(timestampDir);
                if (DateTime.TryParseExact(
                    dirName,
                    "yyyy-MM-ddTHH-mm-ssZ",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dirDate))
                {
                    if (dirDate < cutoff)
                    {
                        try
                        {
                            Directory.Delete(timestampDir, recursive: true);
                            _logger.LogInformation("Pruned old trash entry: {Dir}", timestampDir);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to prune trash entry: {Dir}", timestampDir);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Returns the total size of the trash folder in bytes.</summary>
    public long GetTrashSizeBytes()
    {
        if (!Directory.Exists(_trashRoot)) return 0;
        return Directory.EnumerateFiles(_trashRoot, "*", SearchOption.AllDirectories)
            .Sum(f =>
            {
                try { return new FileInfo(f).Length; }
                catch { return 0L; }
            });
    }
}
