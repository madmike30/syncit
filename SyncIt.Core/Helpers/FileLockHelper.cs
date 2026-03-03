namespace SyncIt.Core.Helpers;

/// <summary>
/// Utilities for detecting and handling locked files.
/// Common scenario: Office apps hold an exclusive lock on .docx/.xlsx while open.
/// </summary>
public static class FileLockHelper
{
    /// <summary>
    /// Returns true if the file is currently locked by another process.
    /// Uses a non-destructive open attempt with FileShare.None.
    /// </summary>
    public static bool IsLocked(string filePath)
    {
        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied — treat as locked for retry purposes
            return true;
        }
    }

    /// <summary>
    /// Returns true if the file is accessible for reading (shared read allowed).
    /// </summary>
    public static bool IsReadable(string filePath)
    {
        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compute exponential backoff delay for retry N.
    /// Delays: 1s, 2s, 4s, 8s, 16s, 32s, 60s (capped).
    /// </summary>
    public static TimeSpan RetryDelay(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));

    /// <summary>Maximum retries before giving up on a locked file.</summary>
    public const int MaxRetries = 5;
}
