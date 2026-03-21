using System.Collections.Concurrent;

namespace SyncIt.Core.Services;

/// <summary>
/// In-memory cache mapping Drive folder IDs to (name, parentId) pairs.
/// Used to resolve relative paths without extra API calls.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class DriveFolderCache
{
    private readonly ConcurrentDictionary<string, FolderInfo> _cache = new();

    /// <summary>Number of cached entries.</summary>
    public int Count => _cache.Count;

    /// <summary>Add or update a folder entry in the cache.</summary>
    public void Set(string folderId, string name, string? parentId)
    {
        _cache[folderId] = new FolderInfo(name, parentId);
    }

    /// <summary>
    /// Try to resolve the relative path of a file/folder from its ID to the root,
    /// using only cached folder data. Returns null if any ancestor is not in the cache.
    /// </summary>
    public string? TryGetRelativePath(string fileId, string fileName, string rootFolderId, string? parentId)
    {
        var segments = new Stack<string>();
        segments.Push(fileName);

        var currentParent = parentId;
        for (int depth = 0; depth < 50; depth++)
        {
            if (currentParent is null || currentParent == rootFolderId)
                return string.Join("/", segments);

            if (!_cache.TryGetValue(currentParent, out var info))
                return null; // cache miss — caller must fall back to API

            segments.Push(info.Name);
            currentParent = info.ParentId;
        }

        return null; // too deep
    }

    /// <summary>
    /// Populate the cache from a batch of files (e.g. during initial sync).
    /// Only folders are cached.
    /// </summary>
    public void PopulateFromFiles(IEnumerable<Google.Apis.Drive.v3.Data.File> files)
    {
        foreach (var file in files)
        {
            if (file.MimeType == "application/vnd.google-apps.folder")
                Set(file.Id, file.Name, file.Parents?.FirstOrDefault());
        }
    }

    /// <summary>Remove a specific entry from the cache.</summary>
    public void Remove(string folderId) => _cache.TryRemove(folderId, out _);

    /// <summary>Clear all cached entries.</summary>
    public void Invalidate() => _cache.Clear();

    private record FolderInfo(string Name, string? ParentId);
}
