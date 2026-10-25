using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Core.Services;

public sealed record DuplicateCopy(string DriveFileId, string RelativePath, DateTime? CreatedUtc, long SizeBytes, bool Suggested, bool Protected, string? ProtectedReason);

public sealed record DuplicateGroup(string FolderPath, string Name, string Md5, IReadOnlyList<DuplicateCopy> Copies)
{
    public long WastedBytes => Copies.Where(c => c.Suggested).Sum(c => c.SizeBytes);
}

/// <summary>
/// Finds duplicate Drive files: same folder, same name, same MD5 (1.4). Suggests the newer extra copies;
/// the oldest copy and any copy SyncIt is linked to are protected. Nothing is trashed automatically.
/// </summary>
public static class DuplicateFinder
{
    public static IReadOnlyList<DuplicateGroup> Find(IEnumerable<(DriveFile File, string RelativePath)> files, ISet<string> linkedIds)
    {
        var groups = files
            .Where(f => f.File.Id is not null && !string.IsNullOrEmpty(f.File.Md5Checksum) && f.File.Trashed != true
                        && f.File.MimeType != "application/vnd.google-apps.folder")
            .GroupBy(f =>
            {
                var rel = f.RelativePath.Replace('\\', '/');
                var slash = rel.LastIndexOf('/');
                var folder = slash < 0 ? "" : rel[..slash];
                return (Folder: folder.ToLowerInvariant(), Name: (f.File.Name ?? "").ToLowerInvariant(), Md5: f.File.Md5Checksum!.ToLowerInvariant());
            })
            .Where(g => g.Count() > 1);

        var result = new List<DuplicateGroup>();
        foreach (var g in groups)
        {
            var ordered = g.OrderBy(f => f.File.CreatedTimeDateTimeOffset ?? DateTimeOffset.MaxValue).ThenBy(f => f.File.Id, StringComparer.Ordinal).ToList();
            // Keep the linked copy if there is one, else the oldest.
            var keep = ordered.FirstOrDefault(f => linkedIds.Contains(f.File.Id!)).File ?? ordered[0].File;
            var copies = ordered.Select(f =>
            {
                var linked = linkedIds.Contains(f.File.Id!);
                var isKeep = ReferenceEquals(f.File, keep);
                return new DuplicateCopy(f.File.Id!, f.RelativePath.Replace('\\', '/'), f.File.CreatedTimeDateTimeOffset?.UtcDateTime,
                    f.File.Size ?? 0, Suggested: !isKeep && !linked, Protected: isKeep || linked,
                    ProtectedReason: linked ? "linked to a file on this PC" : isKeep ? "copy to keep (oldest)" : null);
            }).ToList();
            var first = ordered[0];
            var relFirst = first.RelativePath.Replace('\\', '/');
            var s = relFirst.LastIndexOf('/');
            result.Add(new DuplicateGroup(s < 0 ? "" : relFirst[..s], first.File.Name ?? "", first.File.Md5Checksum!, copies));
        }
        return result.OrderByDescending(r => r.WastedBytes).ThenBy(r => r.FolderPath).ToList();
    }

    /// <summary>The copies that may be trashed: never protected ones, and never every copy of a group.</summary>
    public static IReadOnlyList<DuplicateCopy> SafeToTrash(IEnumerable<DuplicateGroup> groups, ISet<string> selectedIds)
    {
        var list = new List<DuplicateCopy>();
        foreach (var g in groups)
        {
            var pick = g.Copies.Where(c => selectedIds.Contains(c.DriveFileId) && !c.Protected).ToList();
            if (pick.Count >= g.Copies.Count) pick = pick.Skip(1).ToList(); // always keep at least one
            list.AddRange(pick);
        }
        return list;
    }
}
