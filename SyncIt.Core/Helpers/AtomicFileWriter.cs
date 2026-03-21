using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Writes files atomically: stream → .syncit-tmp-{guid} → verify MD5 → rename.
/// No partial files are ever visible to the FileSystemWatcher or other apps.
///
/// Tracks recently-written paths with a 5-second cooldown window so that
/// FileSystemWatcher events caused by our own writes can be suppressed.
/// </summary>
public sealed class AtomicFileWriter
{
    private readonly ILogger<AtomicFileWriter> _logger;

    // Paths currently being written OR recently written (with expiry timestamp).
    // FSW should ignore events for these paths.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentWrites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CooldownWindow = TimeSpan.FromSeconds(5);

    public AtomicFileWriter(ILogger<AtomicFileWriter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Write a stream to <paramref name="targetPath"/> atomically.
    /// Optionally verifies MD5 after write.
    /// </summary>
    /// <param name="sourceStream">Content to write.</param>
    /// <param name="targetPath">Final destination path.</param>
    /// <param name="expectedMd5">Drive MD5 checksum for verification. Null = skip verify.</param>
    /// <param name="setModifiedTime">Set the file's last-write time after rename.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidDataException">MD5 mismatch after download.</exception>
    public async Task WriteAsync(
        Stream      sourceStream,
        string      targetPath,
        string?     expectedMd5,
        DateTime?   setModifiedTime,
        CancellationToken ct = default)
    {
        var tempPath = targetPath + $".syncit-tmp-{Guid.NewGuid():N}";

        try
        {
            PathHelper.EnsureDirectory(Path.GetDirectoryName(targetPath)!);

            // Mark both paths as in-progress (with a far-future expiry during the write)
            var farFuture = DateTimeOffset.UtcNow.AddMinutes(10);
            _recentWrites[tempPath] = farFuture;
            _recentWrites[targetPath] = farFuture;

            // Write to temp file
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await sourceStream.CopyToAsync(fileStream, 81920, ct);
            }

            // Verify MD5 if provided
            if (expectedMd5 is not null)
            {
                var actualMd5 = HashHelper.ComputeMd5(tempPath);
                if (!actualMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"MD5 mismatch for '{targetPath}': expected={expectedMd5} actual={actualMd5}");
                }
            }

            // Set modified time BEFORE rename so FSW sees the correct time
            if (setModifiedTime.HasValue)
                File.SetLastWriteTimeUtc(tempPath, setModifiedTime.Value);

            // Atomic rename — NTFS guarantees this is seen as a single operation
            File.Move(tempPath, targetPath, overwrite: true);

            // Set cooldown window — FSW events arriving within 5 seconds are suppressed
            var cooldownExpiry = DateTimeOffset.UtcNow.Add(CooldownWindow);
            _recentWrites[targetPath] = cooldownExpiry;

            _logger.LogDebug("Atomically wrote {Path}", targetPath);
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp file {Path}", tempPath); }
            }
            throw;
        }
        finally
        {
            // Always remove temp path immediately (it no longer exists after rename)
            _recentWrites.TryRemove(tempPath, out _);
            // targetPath stays in the dict until cooldown expires — cleaned up by IsInProgress()
        }
    }

    /// <summary>
    /// Write to <paramref name="targetPath"/> by streaming directly from a callback.
    /// The callback receives a FileStream to write into (no MemoryStream intermediary).
    /// Critical for large files (50MB+) to avoid heap pressure.
    /// </summary>
    public async Task WriteFromCallbackAsync(
        Func<Stream, Task> writeAction,
        string      targetPath,
        string?     expectedMd5,
        DateTime?   setModifiedTime,
        CancellationToken ct = default)
    {
        var tempPath = targetPath + $".syncit-tmp-{Guid.NewGuid():N}";

        try
        {
            PathHelper.EnsureDirectory(Path.GetDirectoryName(targetPath)!);

            var farFuture = DateTimeOffset.UtcNow.AddMinutes(10);
            _recentWrites[tempPath] = farFuture;
            _recentWrites[targetPath] = farFuture;

            // Create temp file and let the callback write directly into it
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await writeAction(fileStream);
            }

            // Verify MD5 if provided
            if (expectedMd5 is not null)
            {
                var actualMd5 = HashHelper.ComputeMd5(tempPath);
                if (!actualMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"MD5 mismatch for '{targetPath}': expected={expectedMd5} actual={actualMd5}");
                }
            }

            if (setModifiedTime.HasValue)
                File.SetLastWriteTimeUtc(tempPath, setModifiedTime.Value);

            File.Move(tempPath, targetPath, overwrite: true);

            _recentWrites[targetPath] = DateTimeOffset.UtcNow.Add(CooldownWindow);
            _logger.LogDebug("Atomically wrote {Path} (streamed)", targetPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp file {Path}", tempPath); }
            }
            throw;
        }
        finally
        {
            _recentWrites.TryRemove(tempPath, out _);
        }
    }

    /// <summary>
    /// Mark a path as "recently written" so FSW events for it are suppressed.
    /// Use this when moving/deleting a file outside of WriteAsync (e.g. local trash moves).
    /// </summary>
    public void SuppressPath(string path)
    {
        _recentWrites[path] = DateTimeOffset.UtcNow.Add(CooldownWindow);
    }

    /// <summary>
    /// Returns true if the given path was recently written by us (within the cooldown window).
    /// FileSystemWatcher event handlers should ignore events for paths that return true.
    /// Also cleans up expired entries.
    /// </summary>
    private int _cleanupCounter;

    public bool IsInProgress(string path)
    {
        if (_recentWrites.TryGetValue(path, out var expiry))
        {
            if (DateTimeOffset.UtcNow < expiry)
                return true;

            // Expired — clean up
            _recentWrites.TryRemove(path, out _);
        }

        // Every 100 calls, purge all expired entries to prevent unbounded growth
        if (Interlocked.Increment(ref _cleanupCounter) % 100 == 0)
            PurgeExpired();

        return false;
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _recentWrites)
        {
            if (now >= kvp.Value)
                _recentWrites.TryRemove(kvp.Key, out _);
        }
    }
}
