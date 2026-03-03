using System.Security.Cryptography;

namespace SyncIt.Core.Helpers;

/// <summary>
/// File hashing utilities.
/// MD5 is used as the primary hash because Google Drive stores MD5 checksums for all files,
/// allowing direct comparison without re-computing. This is not used for security — only integrity.
/// </summary>
public static class HashHelper
{
    /// <summary>
    /// Compute MD5 of a file. Returns lowercase hex string (matches Drive's md5Checksum field).
    /// Buffers 80KB at a time for memory efficiency.
    /// </summary>
    public static async Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        using var md5 = MD5.Create();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            useAsync: true);

        var hash = await md5.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute MD5 of a stream (used after download for verification).
    /// </summary>
    public static string ComputeMd5(Stream stream)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute MD5 of a file synchronously (for small files or when async is unavailable).
    /// </summary>
    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920);

        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
