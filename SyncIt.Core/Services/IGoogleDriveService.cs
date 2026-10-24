using Google.Apis.Drive.v3.Data;
using SyncIt.Core.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Core.Services;

/// <summary>
/// All interactions with the Google Drive API.
/// Abstracts the Drive v3 client behind an interface for testability.
/// </summary>
public interface IGoogleDriveService
{
    /// <summary>
    /// List all files (recursively) within a Drive folder.
    /// Returns file metadata; does not download content.
    /// </summary>
    IAsyncEnumerable<DriveFile> ListFilesAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// List all files (recursively) within a Drive folder, including their relative paths.
    /// Much faster than ListFilesAsync + GetRelativePathAsync since paths are built during traversal.
    /// </summary>
    IAsyncEnumerable<(DriveFile File, string RelativePath)> ListFilesWithPathsAsync(
        string folderId, string parentPath = "", CancellationToken ct = default);

    /// <summary>
    /// Download a file's content to a stream.
    /// Uses resumable download for files > 5MB.
    /// </summary>
    Task DownloadFileAsync(string fileId, Stream destination, CancellationToken ct = default);

    /// <summary>Download with progress reporting.</summary>
    Task DownloadFileAsync(string fileId, Stream destination, IProgress<long>? progress, CancellationToken ct = default);

    /// <summary>
    /// Export a Google Doc/Sheet/Slide to an Office format.
    /// </summary>
    Task ExportGoogleDocAsync(string fileId, string exportMimeType, Stream destination, CancellationToken ct = default);

    /// <summary>
    /// Upload a new file to a Drive folder.
    /// Returns the new file's Drive metadata.
    /// </summary>
    Task<DriveFile> UploadFileAsync(string localPath, string fileName, string parentFolderId, string mimeType, CancellationToken ct = default);

    /// <summary>Upload with progress reporting.</summary>
    Task<DriveFile> UploadFileAsync(string localPath, string fileName, string parentFolderId, string mimeType, IProgress<long>? progress, CancellationToken ct = default);

    /// <summary>
    /// Update the content of an existing Drive file.
    /// Preserves existing metadata (name, parents, etc.).
    /// </summary>
    Task<DriveFile> UpdateFileAsync(string fileId, string localPath, string mimeType, CancellationToken ct = default);

    /// <summary>Update with progress reporting.</summary>
    Task<DriveFile> UpdateFileAsync(string fileId, string localPath, string mimeType, IProgress<long>? progress, CancellationToken ct = default);

    /// <summary>
    /// Move a Drive file to the Trash (NOT permanent delete).
    /// Files in trash are recoverable for 30 days.
    /// </summary>
    Task TrashFileAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// Get the Drive Changes API start page token.
    /// Used to establish a baseline for change polling.
    /// </summary>
    Task<string> GetStartPageTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// List changes since a given page token.
    /// Returns (changes, nextPageToken).
    /// </summary>
    Task<(IReadOnlyList<Change> Changes, string NextPageToken)> ListChangesAsync(
        string pageToken, CancellationToken ct = default);

    /// <summary>
    /// Get full metadata for a single file.
    /// Returns null if the file doesn't exist or isn't accessible.
    /// </summary>
    Task<DriveFile?> GetFileAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// Get the full path (relative to a root folder) of a Drive file.
    /// Walks parent IDs upward to reconstruct the path.
    /// </summary>
    Task<string?> GetRelativePathAsync(string fileId, string rootFolderId, CancellationToken ct = default);

    /// <summary>
    /// Ensure a folder hierarchy exists on Drive, creating missing folders.
    /// Returns the ID of the leaf folder.
    /// </summary>
    Task<string> EnsureFolderPathAsync(string rootFolderId, string relativeFolderPath, CancellationToken ct = default);

    /// <summary>
    /// List all subfolders within a Drive folder, up to <paramref name="maxDepth"/> levels deep.
    /// Returns a flat list with each entry's name and relative path (e.g. "Work/Reports").
    /// Used by the tray app to let the user browse and select which subfolders to sync.
    /// </summary>
    Task<List<DriveSubfolderInfo>> ListSubfoldersAsync(string folderId, string parentPath = "", int maxDepth = 5, CancellationToken ct = default);

    /// <summary>
    /// Get the authenticated user's account email and storage quota.
    /// </summary>
    Task<DriveAccountInfo> GetAccountInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Find a regular (non-folder, non-Google-Doc, not trashed) file by exact name in a folder.
    /// Used before creating a file so an existing one is reused instead of duplicated.
    /// Returns the newest match, or null.
    /// </summary>
    Task<DriveFile?> FindFileAsync(string parentFolderId, string name, CancellationToken ct = default);

    /// <summary>Rename and/or move a Drive item in place (keeps its ID, history and sharing).</summary>
    Task<DriveFile> MoveFileAsync(string fileId, string newName, string newParentId, CancellationToken ct = default);
}

/// <summary>Drive subfolder descriptor returned by ListSubfoldersAsync.</summary>
public record DriveSubfolderInfo(string Name, string RelativePath, string DriveId);

/// <summary>Drive account info (email and storage quota).</summary>
public record DriveAccountInfo(string Email, long StorageUsedBytes, long StorageTotalBytes);
