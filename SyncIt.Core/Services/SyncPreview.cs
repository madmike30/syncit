using SyncIt.Core.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Core.Services;

public enum PreviewAction { Upload, Download, Match, Conflict, KeepOnDrive, Skip }

public sealed record PreviewItem(PreviewAction Action, string RelativePath, long SizeBytes, string? Note = null);

/// <summary>What a folder's first sync will do (1.4 dry-run). Nothing is ever deleted on a first sync.</summary>
public sealed class SyncPreviewResult
{
    public List<PreviewItem> Items { get; } = new();
    public int Count(PreviewAction a) => Items.Count(i => i.Action == a);
    public long Bytes(PreviewAction a) => Items.Where(i => i.Action == a).Sum(i => i.SizeBytes);
    public int Deletes => 0;
    public string Summary =>
        $"{Count(PreviewAction.Upload)} to upload, {Count(PreviewAction.Download)} to download, " +
        $"{Count(PreviewAction.Match)} already identical, {Count(PreviewAction.Conflict)} different on both sides, 0 deletions";
}

public sealed record LocalEntry(string RelativePath, long SizeBytes);

public static class SyncPreview
{
    /// <summary>
    /// Plans the first sync. <paramref name="localMd5"/> is only called for files with the same path and size
    /// on both sides (cheap for the usual case).
    /// </summary>
    public static async Task<SyncPreviewResult> BuildAsync(
        SyncPair pair,
        IEnumerable<LocalEntry> local,
        IEnumerable<(DriveFile File, string RelativePath)> remote,
        Func<string, bool> isIncluded,
        Func<string, Task<string>> localMd5)
    {
        var result = new SyncPreviewResult();
        var localMap = new Dictionary<string, LocalEntry>(Helpers.PathKey.Comparer);
        foreach (var l in local)
        {
            var rel = l.RelativePath.Replace('\\', '/');
            if (!isIncluded(rel)) continue;
            if (l.SizeBytes > pair.MaxFileSizeBytes) { result.Items.Add(new PreviewItem(PreviewAction.Skip, rel, l.SizeBytes, "larger than the size limit")); continue; }
            localMap[rel] = l with { RelativePath = rel };
        }

        var seen = new HashSet<string>(Helpers.PathKey.Comparer);
        foreach (var (file, rawRel) in remote)
        {
            if (file.MimeType == "application/vnd.google-apps.folder") continue;
            var rel = rawRel.Replace('\\', '/');
            var isGoogle = GoogleDocExportMap.IsGoogleDoc(file.MimeType);
            if (isGoogle)
            {
                if (!pair.ExportGoogleDocs || !GoogleDocExportMap.TryGetExport(file.MimeType, pair, out _, out var ext)) continue;
                if (!rel.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) rel += ext;
            }
            else if (file.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) == true) continue;
            if (!isIncluded(rel)) continue;
            seen.Add(rel);

            if (localMap.TryGetValue(rel, out var l))
            {
                if (isGoogle) { result.Items.Add(new PreviewItem(PreviewAction.Match, rel, l.SizeBytes, "exported Google file")); continue; }
                var same = file.Size == l.SizeBytes && file.Md5Checksum is { } md5 &&
                           (await localMd5(rel)).Equals(md5, StringComparison.OrdinalIgnoreCase);
                result.Items.Add(same
                    ? new PreviewItem(PreviewAction.Match, rel, l.SizeBytes)
                    : new PreviewItem(PreviewAction.Conflict, rel, l.SizeBytes,
                        pair.Mode == SyncMode.BackupOnly ? "the PC copy replaces the Drive copy (old one stays in Drive version history)"
                                                         : $"resolved by rule {pair.ConflictPolicy.Resolution}"));
            }
            else
            {
                result.Items.Add(pair.Mode == SyncMode.BackupOnly
                    ? new PreviewItem(PreviewAction.KeepOnDrive, rel, file.Size ?? 0, "backup only: stays on Drive")
                    : new PreviewItem(PreviewAction.Download, rel, file.Size ?? 0, isGoogle ? "exported Google file" : null));
            }
        }
        foreach (var (rel, l) in localMap)
            if (!seen.Contains(rel)) result.Items.Add(new PreviewItem(PreviewAction.Upload, rel, l.SizeBytes));
        result.Items.Sort((a, b) => a.Action != b.Action ? a.Action.CompareTo(b.Action) : string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }

    public static IEnumerable<LocalEntry> ScanLocal(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            yield return new LocalEntry(Path.GetRelativePath(root, f.FullName), f.Length);
    }
}
