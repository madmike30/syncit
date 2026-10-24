using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Upload;
using Microsoft.Extensions.Logging;
using Polly;
using SyncIt.Core.Helpers;
using SyncIt.Core.IO;
using SyncIt.Core.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Core.Services;

/// <summary>
/// Google Drive API v3 client wrapper.
/// All calls are wrapped with Polly retry for transient failures (429, 503, network errors).
/// </summary>
public sealed class GoogleDriveService : IGoogleDriveService, IDisposable
{
    private readonly DriveService _drive;
    private readonly ILogger<GoogleDriveService> _logger;
    private readonly AsyncPolicy _retryPolicy;
    private readonly AsyncPolicy _uploadRetryPolicy;
    private readonly AsyncPolicy _downloadRetryPolicy;
    private readonly SyncConfig? _config;
    private readonly DriveFolderCache _folderCache = new();
    private readonly AsyncKeyedLocker _folderEnsureLocks = new(StringComparer.OrdinalIgnoreCase);
    private string? _resolvedRootId;

    // Fields requested on every file list/get (keep minimal to reduce response size)
    private const string FileFields = "id,name,mimeType,md5Checksum,size,modifiedTime,parents,trashed,headRevisionId";
    private const string ListFields = $"nextPageToken,files({FileFields})";

    public GoogleDriveService(UserCredential credential, ILogger<GoogleDriveService> logger)
        : this(credential, logger, null)
    {
    }

    public GoogleDriveService(UserCredential credential, ILogger<GoogleDriveService> logger, SyncConfig? config)
    {
        _logger = logger;
        _config = config;
        _drive  = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "SyncIt",
        });
        _retryPolicy         = RetryHelper.CreateDrivePolicy(logger, config?.MetadataRetryPolicy);
        _uploadRetryPolicy   = RetryHelper.CreateDrivePolicy(logger, config?.UploadRetryPolicy);
        _downloadRetryPolicy = RetryHelper.CreateDrivePolicy(logger, config?.DownloadRetryPolicy);
    }

    private long UploadBytesPerSecond => (_config?.MaxUploadKBps ?? 0) * 1024L;
    private long DownloadBytesPerSecond => (_config?.MaxDownloadKBps ?? 0) * 1024L;

    /// <summary>Folder cache for resolving relative paths without extra API calls.</summary>
    public DriveFolderCache FolderCache => _folderCache;

    // -------------------------------------------------------------------------
    // File listing
    // -------------------------------------------------------------------------

    public async IAsyncEnumerable<DriveFile> ListFilesAsync(
        string folderId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? pageToken = null;

        do
        {
            var request = _drive.Files.List();
            request.Q           = $"'{folderId}' in parents and trashed = false";
            request.Fields      = ListFields;
            request.PageSize    = 1000;
            request.IncludeItemsFromAllDrives = false;
            if (pageToken is not null) request.PageToken = pageToken;

            var result = await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
            pageToken  = result.NextPageToken;

            if (result.Files is null) break;

            foreach (var file in result.Files)
            {
                if (file.Trashed == true) continue;

                // Cache folder entries for fast path resolution
                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    _folderCache.Set(file.Id!, PathHelper.SanitizeRemoteSegment(file.Name ?? file.Id ?? "unnamed"), file.Parents?.FirstOrDefault());

                    // Yield the folder itself so callers can create it locally
                    yield return file;

                    // Recurse into subfolder
                    await foreach (var child in ListFilesAsync(file.Id!, ct))
                        yield return child;
                }
                else
                {
                    yield return file;
                }
            }
        }
        while (pageToken is not null);
    }

    public async IAsyncEnumerable<(DriveFile File, string RelativePath)> ListFilesWithPathsAsync(
        string folderId,
        string parentPath = "",
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? pageToken = null;

        do
        {
            var request = _drive.Files.List();
            request.Q           = $"'{folderId}' in parents and trashed = false";
            request.Fields      = ListFields;
            request.PageSize    = 1000;
            request.IncludeItemsFromAllDrives = false;
            if (pageToken is not null) request.PageToken = pageToken;

            var result = await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
            pageToken  = result.NextPageToken;

            if (result.Files is null) break;

            foreach (var file in result.Files)
            {
                if (file.Trashed == true) continue;

                var relativePath = PathHelper.CombineRemoteRelativePath(parentPath, file.Name ?? file.Id ?? "unnamed");

                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    _folderCache.Set(file.Id!, PathHelper.SanitizeRemoteSegment(file.Name ?? file.Id ?? "unnamed"), file.Parents?.FirstOrDefault());
                    yield return (file, relativePath);

                    await foreach (var child in ListFilesWithPathsAsync(file.Id!, relativePath, ct))
                        yield return child;
                }
                else
                {
                    yield return (file, relativePath);
                }
            }
        }
        while (pageToken is not null);
    }

    // -------------------------------------------------------------------------
    // Download
    // -------------------------------------------------------------------------

    public Task DownloadFileAsync(string fileId, Stream destination, CancellationToken ct = default)
        => DownloadFileAsync(fileId, destination, null, ct);

    public async Task DownloadFileAsync(string fileId, Stream destination, IProgress<long>? progress, CancellationToken ct = default)
    {
        var request = _drive.Files.Get(fileId);
        request.Fields = FileFields;

        if (progress is not null)
            request.MediaDownloader.ProgressChanged += p => progress.Report(p.BytesDownloaded);

        // Wrap destination with throttle if configured
        var target = DownloadBytesPerSecond > 0
            ? new ThrottledStream(destination, DownloadBytesPerSecond)
            : destination;

        await _downloadRetryPolicy.ExecuteAsync(async () =>
        {
            var dlProgress = await request.DownloadAsync(target, ct);
            if (dlProgress.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw new IOException($"Download did not complete: {dlProgress.Status} — {dlProgress.Exception?.Message}");
        });
    }

    public async Task ExportGoogleDocAsync(string fileId, string exportMimeType, Stream destination, CancellationToken ct = default)
    {
        var request = _drive.Files.Export(fileId, exportMimeType);

        await _downloadRetryPolicy.ExecuteAsync(async () =>
        {
            var progress = await request.DownloadAsync(destination, ct);
            if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw new IOException($"Export did not complete: {progress.Status} — {progress.Exception?.Message}");
        });
    }

    // -------------------------------------------------------------------------
    // Upload
    // -------------------------------------------------------------------------

    public Task<DriveFile> UploadFileAsync(string localPath, string fileName, string parentFolderId, string mimeType, CancellationToken ct = default)
        => UploadFileAsync(localPath, fileName, parentFolderId, mimeType, null, ct);

    public async Task<DriveFile> UploadFileAsync(
        string localPath,
        string fileName,
        string parentFolderId,
        string mimeType,
        IProgress<long>? progress,
        CancellationToken ct = default)
    {
        var fileMetadata = new DriveFile
        {
            Name    = fileName,
            Parents = new[] { parentFolderId },
        };

        return await _uploadRetryPolicy.ExecuteAsync(async () =>
        {
            Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            if (UploadBytesPerSecond > 0)
                stream = new ThrottledStream(stream, UploadBytesPerSecond);
            await using (stream)
            {
                var request = _drive.Files.Create(fileMetadata, stream, mimeType);
                request.Fields = FileFields;

                if (progress is not null)
                    request.ProgressChanged += p => progress.Report(p.BytesSent);

                var uploadProgress = await request.UploadAsync(ct);
                if (uploadProgress.Status != UploadStatus.Completed)
                    throw new IOException($"Upload failed: {uploadProgress.Status} — {uploadProgress.Exception?.Message}");

                return request.ResponseBody;
            }
        });
    }

    public Task<DriveFile> UpdateFileAsync(string fileId, string localPath, string mimeType, CancellationToken ct = default)
        => UpdateFileAsync(fileId, localPath, mimeType, null, ct);

    public async Task<DriveFile> UpdateFileAsync(
        string fileId,
        string localPath,
        string mimeType,
        IProgress<long>? progress,
        CancellationToken ct = default)
    {
        return await _uploadRetryPolicy.ExecuteAsync(async () =>
        {
            Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            if (UploadBytesPerSecond > 0)
                stream = new ThrottledStream(stream, UploadBytesPerSecond);
            await using (stream)
            {
                var request = _drive.Files.Update(new DriveFile(), fileId, stream, mimeType);
                request.Fields = FileFields;

                if (progress is not null)
                    request.ProgressChanged += p => progress.Report(p.BytesSent);

                var uploadProgress = await request.UploadAsync(ct);
                if (uploadProgress.Status != UploadStatus.Completed)
                    throw new IOException($"Update upload failed: {uploadProgress.Status} — {uploadProgress.Exception?.Message}");

                return request.ResponseBody;
            }
        });
    }

    // -------------------------------------------------------------------------
    // Delete (soft — Drive Trash only)
    // -------------------------------------------------------------------------

    public async Task TrashFileAsync(string fileId, CancellationToken ct = default)
    {
        try
        {
            var update = new DriveFile { Trashed = true };
            await _retryPolicy.ExecuteAsync(() =>
            {
                var request = _drive.Files.Update(update, fileId);
                request.Fields = "id,trashed";
                return request.ExecuteAsync(ct);
            });
            _logger.LogInformation("Trashed Drive file {FileId}", fileId);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // File already deleted — treat as success (idempotent)
            _logger.LogDebug("Drive file {FileId} already deleted (404 on trash).", fileId);
        }
    }

    // -------------------------------------------------------------------------
    // Change detection
    // -------------------------------------------------------------------------

    public async Task<string> GetStartPageTokenAsync(CancellationToken ct = default)
    {
        var result = await _retryPolicy.ExecuteAsync(() =>
            _drive.Changes.GetStartPageToken().ExecuteAsync(ct));
        return result.StartPageTokenValue;
    }

    public async Task<(IReadOnlyList<Change> Changes, string NextPageToken)> ListChangesAsync(
        string pageToken,
        CancellationToken ct = default)
    {
        var allChanges  = new List<Change>();
        var currentToken = pageToken;

        while (true)
        {
            var request  = _drive.Changes.List(currentToken);
            request.Fields     = "nextPageToken,newStartPageToken,changes(fileId,removed,file(" + FileFields + "))";
            request.PageSize   = 200;
            request.Spaces     = "drive";
            request.IncludeItemsFromAllDrives = false;

            var result = await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));

            if (result.Changes is not null)
            {
                allChanges.AddRange(result.Changes);

                // Update folder cache from changes
                foreach (var change in result.Changes)
                {
                    if (change.File is not null && change.File.MimeType == "application/vnd.google-apps.folder")
                    {
                        if (change.Removed == true || change.File.Trashed == true)
                            _folderCache.Remove(change.FileId!);
                        else
                            _folderCache.Set(change.FileId!, PathHelper.SanitizeRemoteSegment(change.File.Name ?? change.FileId ?? "unnamed"), change.File.Parents?.FirstOrDefault());
                    }
                }
            }

            if (result.NewStartPageToken is not null)
            {
                // We've consumed all pages — NewStartPageToken is the new baseline
                return (allChanges, result.NewStartPageToken);
            }

            if (result.NextPageToken is null)
                break;

            currentToken = result.NextPageToken;
        }

        return (allChanges, currentToken);
    }

    // -------------------------------------------------------------------------
    // Metadata
    // -------------------------------------------------------------------------

    public async Task<DriveFile?> FindFileAsync(string parentFolderId, string name, CancellationToken ct = default)
    {
        var request = _drive.Files.List();
        request.Q = $"'{parentFolderId}' in parents and name = '{EscapeDriveQuery(name)}' and trashed = false " +
                    "and not mimeType contains 'application/vnd.google-apps.'";
        request.Fields = $"files({FileFields})";
        request.OrderBy = "modifiedTime desc";
        request.PageSize = 10;
        var result = await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
        // Drive's name match is case-insensitive; prefer an exact match.
        return result.Files?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
            ?? result.Files?.FirstOrDefault();
    }

    public async Task<DriveFile> MoveFileAsync(string fileId, string newName, string newParentId, CancellationToken ct = default)
    {
        var current = await GetFileAsync(fileId, ct) ?? throw new FileNotFoundException($"Drive file {fileId} not found.");
        var request = _drive.Files.Update(new DriveFile { Name = newName }, fileId);
        request.Fields = FileFields;
        if (newParentId == "root")
            newParentId = (await GetFileAsync("root", ct))?.Id ?? newParentId;
        var oldParents = current.Parents ?? Array.Empty<string>();
        if (!oldParents.Contains(newParentId))
        {
            request.AddParents = newParentId;
            if (oldParents.Count > 0) request.RemoveParents = string.Join(",", oldParents);
        }
        return await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
    }

    public async Task<DriveFile?> GetFileAsync(string fileId, CancellationToken ct = default)
    {
        try
        {
            var request = _drive.Files.Get(fileId);
            request.Fields = FileFields;
            return await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string?> GetRelativePathAsync(string fileId, string rootFolderId, CancellationToken ct = default)
    {
        // Resolve "root" alias to actual Drive root folder ID
        if (rootFolderId == "root")
        {
            if (_resolvedRootId is null)
            {
                var rootFile = await GetFileAsync("root", ct);
                _resolvedRootId = rootFile?.Id ?? "root";
            }
            rootFolderId = _resolvedRootId;
        }

        // If this IS the root folder, skip it — it's not "inside" itself
        if (fileId == rootFolderId) return null;

        // Fetch file metadata (needed for name + parent)
        var file = await GetFileAsync(fileId, ct);
        if (file is null) return null;
        var safeFileName = PathHelper.SanitizeRemoteSegment(file.Name ?? fileId);

        // Try cache-only resolution first (avoids parent API calls)
        var cachedPath = _folderCache.TryGetRelativePath(
            fileId, safeFileName, rootFolderId, file.Parents?.FirstOrDefault());
        if (cachedPath is not null)
            return cachedPath;

        // Cache miss — walk up the tree via API (and populate cache along the way)
        var segments = new Stack<string>();
        segments.Push(safeFileName);

        var parentId = file.Parents?.FirstOrDefault();
        if (parentId is null) return null;

        bool reachedRoot = (parentId == rootFolderId);
        for (int depth = 0; depth < 50 && !reachedRoot && parentId is not null; depth++)
        {
            var parent = await GetFileAsync(parentId, ct);
            if (parent is null) return null;

            _folderCache.Set(parentId, PathHelper.SanitizeRemoteSegment(parent.Name ?? parentId), parent.Parents?.FirstOrDefault());

            // After caching this parent, try full cache resolution from the original file
            var cached = _folderCache.TryGetRelativePath(fileId, safeFileName, rootFolderId, file.Parents?.FirstOrDefault());
            if (cached is not null) return cached;

            segments.Push(PathHelper.SanitizeRemoteSegment(parent.Name ?? parentId));
            parentId = parent.Parents?.FirstOrDefault();
            reachedRoot = (parentId == rootFolderId);
        }

        if (!reachedRoot) return null;

        return string.Join("/", segments);
    }

    public async Task<string> EnsureFolderPathAsync(string rootFolderId, string relativeFolderPath, CancellationToken ct = default)
    {
        var parts = relativeFolderPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentId = rootFolderId;

        foreach (var part in parts)
        {
            var currentParentId = parentId;
            var lockKey = $"{currentParentId}\n{part}";

            parentId = await _folderEnsureLocks.RunAsync(lockKey, async () =>
            {
                // Re-check inside the lock so concurrent uploads cannot create duplicates.
                var listRequest = _drive.Files.List();
                listRequest.Q        = $"'{currentParentId}' in parents and name = '{EscapeDriveQuery(part)}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
                listRequest.Fields   = "files(id,name,parents)";
                listRequest.PageSize = 1;

                var result   = await _retryPolicy.ExecuteAsync(() => listRequest.ExecuteAsync(ct));
                var existing = result.Files?.FirstOrDefault();

                if (existing is not null)
                {
                    _folderCache.Set(existing.Id, existing.Name, existing.Parents?.FirstOrDefault() ?? currentParentId);
                    return existing.Id;
                }

                var folder = new DriveFile
                {
                    Name     = part,
                    Parents  = new[] { currentParentId },
                    MimeType = "application/vnd.google-apps.folder",
                };

                var created = await _retryPolicy.ExecuteAsync(() =>
                {
                    var req = _drive.Files.Create(folder);
                    req.Fields = "id,name,parents";
                    return req.ExecuteAsync(ct);
                });

                _folderCache.Set(created.Id, created.Name ?? part, created.Parents?.FirstOrDefault() ?? currentParentId);
                _logger.LogDebug("Created Drive folder '{Name}' under {ParentId} as {FolderId}", part, currentParentId, created.Id);
                return created.Id;
            }, ct);
        }

        return parentId;
    }

    public async Task<List<DriveSubfolderInfo>> ListSubfoldersAsync(
        string folderId,
        string parentPath = "",
        int maxDepth = 5,
        CancellationToken ct = default)
    {
        var result = new List<DriveSubfolderInfo>();
        if (maxDepth <= 0) return result;

        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Q = $"'{folderId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            request.Fields = "nextPageToken,files(id,name)";
            request.PageSize = 200;
            request.OrderBy = "name";
            if (pageToken is not null) request.PageToken = pageToken;

            var page = await _retryPolicy.ExecuteAsync(() => request.ExecuteAsync(ct));
            pageToken = page.NextPageToken;

            if (page.Files is null) break;

            foreach (var folder in page.Files)
            {
                var relativePath = PathHelper.CombineRemoteRelativePath(parentPath, folder.Name ?? folder.Id ?? "unnamed");

                result.Add(new DriveSubfolderInfo(folder.Name ?? folder.Id ?? "unnamed", relativePath, folder.Id ?? string.Empty));

                // Recurse into subfolder
                var children = await ListSubfoldersAsync(folder.Id!, relativePath, maxDepth - 1, ct);
                result.AddRange(children);
            }
        }
        while (pageToken is not null);

        return result;
    }

    public async Task<DriveAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var aboutRequest = _drive.About.Get();
        aboutRequest.Fields = "user(emailAddress),storageQuota(usage,limit)";
        var about = await _retryPolicy.ExecuteAsync(() => aboutRequest.ExecuteAsync(ct));

        var email = about.User?.EmailAddress ?? "Unknown";
        var used  = about.StorageQuota?.Usage ?? 0;
        var total = about.StorageQuota?.Limit ?? 0; // 0 = unlimited (e.g. Workspace)

        return new DriveAccountInfo(email, used, total);
    }

    private static string EscapeDriveQuery(string value) =>
        value.Replace("'", "\\'");

    public void Dispose() => _drive.Dispose();
}
