using System.Collections.Concurrent;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Tracks per-file transfer progress for all active uploads/downloads.
/// Raises ProgressChanged event for IPC push to tray/UI.
/// </summary>
public sealed class TransferTracker
{
    private readonly ConcurrentDictionary<string, FileTransferProgress> _active = new();

    public event Action<FileTransferProgress>? ProgressChanged;

    public IReadOnlyCollection<FileTransferProgress> ActiveTransfers => _active.Values.ToList();

    /// <summary>
    /// Begin tracking a file transfer. Returns an IProgress that updates BytesTransferred.
    /// </summary>
    public IProgress<long> Start(string pairId, string relativePath, TransferDirection direction, long totalBytes)
    {
        var key = $"{pairId}:{relativePath}";
        var ftp = new FileTransferProgress
        {
            SyncPairId   = pairId,
            RelativePath = relativePath,
            Direction    = direction,
            State        = TransferState.InProgress,
            TotalBytes   = totalBytes,
            StartedUtc   = DateTime.UtcNow,
        };
        _active[key] = ftp;
        ProgressChanged?.Invoke(ftp);

        return new Progress<long>(bytes =>
        {
            ftp.BytesTransferred = bytes;
            ProgressChanged?.Invoke(ftp);
        });
    }

    public void Complete(string pairId, string relativePath)
    {
        var key = $"{pairId}:{relativePath}";
        if (_active.TryRemove(key, out var ftp))
        {
            ftp.State = TransferState.Completed;
            ftp.BytesTransferred = ftp.TotalBytes;
            ProgressChanged?.Invoke(ftp);
        }
    }

    public void Fail(string pairId, string relativePath, string error)
    {
        var key = $"{pairId}:{relativePath}";
        if (_active.TryRemove(key, out var ftp))
        {
            ftp.State = TransferState.Failed;
            ftp.ErrorMessage = error;
            ProgressChanged?.Invoke(ftp);
        }
    }
}
