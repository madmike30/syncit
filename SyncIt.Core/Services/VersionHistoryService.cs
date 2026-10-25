using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Core.Services;

/// <summary>A file in the Drive trash that belongs to a sync folder.</summary>
public sealed record TrashedItem(string DriveFileId, string RelativePath, bool IsFolder, long SizeBytes, DateTime? TrashedUtc);

/// <summary>
/// Version history and restore (1.4). Restoring a revision never loses data: the current local copy
/// goes to the SyncIt trash first, then the old content is written in place and syncs up as a new version.
/// </summary>
public sealed class VersionHistoryService
{
    private readonly IGoogleDriveService _drive;
    private readonly IFileStateStore _store;
    private readonly LocalTrashManager _trash;
    private readonly ILogger _logger;

    public VersionHistoryService(IGoogleDriveService drive, IFileStateStore store, LocalTrashManager trash, ILogger logger)
    {
        _drive = drive; _store = store; _trash = trash; _logger = logger;
    }

    /// <summary>The Drive file behind a local path, or null when SyncIt has not synced it.</summary>
    public async Task<FileRecord?> FindRemoteAsync(SyncPair pair, string relativePath) =>
        await _store.GetRecordAsync(pair.Id, SyncSide.Remote, relativePath.Replace('\\', '/'));

    public async Task<IReadOnlyList<DriveRevisionInfo>> GetRevisionsAsync(SyncPair pair, string relativePath, CancellationToken ct = default)
    {
        var rec = await FindRemoteAsync(pair, relativePath)
                  ?? throw new InvalidOperationException("This file has not been synced to Google Drive yet.");
        if (ExportedFiles.IsExportOf(rec))
            throw new InvalidOperationException("This is an exported Google file. Use Version history in Google Docs/Sheets/Slides (Open in Drive).");
        return await _drive.ListRevisionsAsync(rec.DriveFileId!, ct);
    }

    /// <summary>Writes the chosen revision over the local file. Returns where the previous local copy was saved.</summary>
    public async Task<string?> RestoreRevisionAsync(SyncPair pair, string relativePath, DriveRevisionInfo revision, CancellationToken ct = default)
    {
        var rec = await FindRemoteAsync(pair, relativePath)
                  ?? throw new InvalidOperationException("This file has not been synced to Google Drive yet.");
        var target = PathHelper.ToAbsolute(pair.LocalPath, relativePath);
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + ".syncit-tmp-restore");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await _drive.DownloadRevisionAsync(rec.DriveFileId!, revision.Id, fs, ct);
                await fs.FlushAsync(ct);
            }
            if (!string.IsNullOrEmpty(revision.Md5))
            {
                var got = await HashHelper.ComputeMd5Async(temp, ct);
                if (!got.Equals(revision.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Downloaded revision failed its checksum; nothing was changed.");
            }
            string? saved = null;
            if (File.Exists(target)) saved = _trash.MoveToTrash(pair.Id, target, relativePath);
            File.Move(temp, target);
            _logger.LogInformation("Restored revision {Rev} of '{Path}' (previous copy saved to {Saved}).", revision.Id, relativePath, saved);
            return saved;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Trashed Drive items that lived inside this sync folder.</summary>
    public async Task<IReadOnlyList<TrashedItem>> ListTrashAsync(SyncPair pair, CancellationToken ct = default)
    {
        var result = new List<TrashedItem>();
        foreach (var f in await _drive.ListTrashedAsync(ct))
        {
            if (f.Id is null) continue;
            string? rel;
            try { rel = await _drive.GetRelativePathAsync(f.Id, pair.RemoteFolderId, ct); }
            catch { rel = null; }
            if (string.IsNullOrEmpty(rel)) continue;
            result.Add(new TrashedItem(f.Id, rel, f.MimeType == "application/vnd.google-apps.folder", f.Size ?? 0,
                f.TrashedTimeDateTimeOffset?.UtcDateTime));
        }
        return result.OrderByDescending(t => t.TrashedUtc).ToList();
    }

    /// <summary>Takes an item out of the Drive trash; the normal sync then brings it back to this PC.</summary>
    public Task RestoreFromTrashAsync(TrashedItem item, CancellationToken ct = default) => _drive.UntrashFileAsync(item.DriveFileId, ct);
}

/// <summary>Drive web links for synced files and folders (Explorer "Copy Drive link" / "Open in Drive").</summary>
public static class DriveLinks
{
    public static string For(string fileId, string? mimeType) => mimeType switch
    {
        "application/vnd.google-apps.folder" => $"https://drive.google.com/drive/folders/{fileId}",
        GoogleDocExportMap.DocMime => $"https://docs.google.com/document/d/{fileId}/edit",
        GoogleDocExportMap.SheetMime => $"https://docs.google.com/spreadsheets/d/{fileId}/edit",
        GoogleDocExportMap.SlidesMime => $"https://docs.google.com/presentation/d/{fileId}/edit",
        _ => $"https://drive.google.com/file/d/{fileId}/view",
    };

    /// <summary>The pair that contains <paramref name="absolutePath"/> and the path relative to it.</summary>
    public static (SyncPair Pair, string RelativePath)? Locate(SyncConfig config, string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath).TrimEnd('\\');
        foreach (var p in config.SyncPairs.Where(p => p.Enabled).OrderByDescending(p => p.LocalPath.Length))
        {
            var root = Path.GetFullPath(PathHelper.Expand(p.LocalPath)).TrimEnd('\\');
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return (p, "");
            if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return (p, full[(root.Length + 1)..].Replace('\\', '/'));
        }
        return null;
    }

    /// <summary>Resolves the Drive link for a local file or folder. Returns null when it is not on Drive (yet).</summary>
    public static async Task<string?> ResolveAsync(SyncConfig config, string absolutePath, IFileStateStore store,
        Func<SyncPair, IGoogleDriveService?> driveFor, CancellationToken ct = default)
    {
        if (Locate(config, absolutePath) is not { } hit) return null;
        var (pair, rel) = hit;
        if (rel.Length == 0) return For(pair.RemoteFolderId == "root" ? "root" : pair.RemoteFolderId, "application/vnd.google-apps.folder")
            .Replace("folders/root", "my-drive");
        if (await store.GetRecordAsync(pair.Id, SyncSide.Remote, rel) is { DriveFileId: not null } rec)
            return For(rec.DriveFileId, rec.MimeType);
        if (!Directory.Exists(absolutePath) || driveFor(pair) is not { } drive) return null;
        var id = pair.RemoteFolderId;
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = await drive.FindFileAsync(id, seg, ct);
            if (f?.Id is null || f.MimeType != "application/vnd.google-apps.folder") return null;
            id = f.Id;
        }
        return For(id, "application/vnd.google-apps.folder");
    }
}
