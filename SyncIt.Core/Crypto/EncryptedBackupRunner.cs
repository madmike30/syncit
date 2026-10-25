using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Services;

namespace SyncIt.Core.Crypto;

/// <summary>
/// EXPERIMENTAL (beta). One-way encrypted backup of a local folder to a Drive folder.
/// Safety rules: never deletes or changes anything on this PC; never deletes on Drive
/// (a changed file replaces its own encrypted copy, older versions stay in Drive version history);
/// restore always writes into a NEW, empty folder.
/// </summary>
public sealed class EncryptedBackupRunner
{
    private readonly IGoogleDriveService _drive;
    private readonly VaultCrypto _crypto;
    private readonly string _manifestPath;
    private readonly ILogger _logger;

    public sealed record ManifestEntry(long Size, long MtimeTicks, string DriveFileId);
    public sealed record RunResult(int Uploaded, int Unchanged, int Failed);

    public EncryptedBackupRunner(IGoogleDriveService drive, VaultCrypto crypto, string manifestPath, ILogger logger)
    {
        _drive = drive; _crypto = crypto; _manifestPath = manifestPath; _logger = logger;
    }

    public Dictionary<string, ManifestEntry> LoadManifest()
    {
        try
        {
            return File.Exists(_manifestPath)
                ? JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(File.ReadAllText(_manifestPath)) ?? new()
                : new();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Encrypted backup manifest unreadable; re-checking every file."); return new(); }
    }

    private void SaveManifest(Dictionary<string, ManifestEntry> m)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        var tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m));
        File.Move(tmp, _manifestPath, overwrite: true);
    }

    public async Task<RunResult> BackupAsync(string localRoot, string remoteRootId, Func<string, bool> isIncluded, CancellationToken ct = default)
    {
        var manifest = LoadManifest();
        int up = 0, same = 0, failed = 0;
        foreach (var entry in SyncPreview.ScanLocal(localRoot))
        {
            ct.ThrowIfCancellationRequested();
            var rel = entry.RelativePath.Replace('\\', '/');
            if (!isIncluded(rel)) continue;
            var full = Path.Combine(localRoot, entry.RelativePath);
            var mtime = File.GetLastWriteTimeUtc(full).Ticks;
            if (manifest.TryGetValue(rel, out var m) && m.Size == entry.SizeBytes && m.MtimeTicks == mtime) { same++; continue; }

            var tmp = Path.Combine(Path.GetTempPath(), "syncit-enc-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                    _crypto.Encrypt(input, output);

                var encPath = _crypto.EncryptPath(rel);
                var slash = encPath.LastIndexOf('/');
                var parent = slash < 0 ? remoteRootId : await _drive.EnsureFolderPathAsync(remoteRootId, encPath[..slash], ct);
                var encName = slash < 0 ? encPath : encPath[(slash + 1)..];
                var existing = m?.DriveFileId is { } id ? id : (await _drive.FindFileAsync(parent, encName, ct))?.Id;
                var file = existing is not null
                    ? await _drive.UpdateFileAsync(existing, tmp, "application/octet-stream", ct)
                    : await _drive.UploadFileAsync(tmp, encName, parent, "application/octet-stream", ct);
                manifest[rel] = new ManifestEntry(entry.SizeBytes, mtime, file.Id);
                up++;
                if (up % 20 == 0) SaveManifest(manifest);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; _logger.LogWarning(ex, "Encrypted backup failed for '{Path}'", rel); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        SaveManifest(manifest);
        _logger.LogInformation("Encrypted backup: {Up} uploaded, {Same} unchanged, {Failed} failed", up, same, failed);
        return new RunResult(up, same, failed);
    }

    /// <summary>Decrypts the whole Drive folder into <paramref name="targetFolder"/>, which must be new or empty.</summary>
    public async Task<int> RestoreAsync(string remoteRootId, string targetFolder, CancellationToken ct = default)
    {
        if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
            throw new IOException("Choose an empty folder to restore into — SyncIt never overwrites files when restoring.");
        Directory.CreateDirectory(targetFolder);
        int n = 0;
        foreach (var (file, encRel) in await _drive.ListTreeAsync(remoteRootId, ct))
        {
            if (file.MimeType == "application/vnd.google-apps.folder" || file.Id is null) continue;
            var rel = _crypto.DecryptPath(encRel);
            var dest = Path.GetFullPath(Path.Combine(targetFolder, rel));
            if (!dest.StartsWith(Path.GetFullPath(targetFolder), StringComparison.OrdinalIgnoreCase)) continue; // path traversal guard
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var cipher = new MemoryStream();
            await _drive.DownloadFileAsync(file.Id, cipher, ct);
            cipher.Position = 0;
            await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
            _crypto.Decrypt(cipher, output);
            n++;
        }
        return n;
    }
}
