namespace SyncIt.Core.Models;

public enum TransferDirection { Upload, Download }
public enum TransferState { Queued, InProgress, Completed, Failed }

/// <summary>
/// Per-file transfer progress snapshot. Used by TransferTracker and pushed to tray via IPC.
/// </summary>
public class FileTransferProgress
{
    public string SyncPairId   { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public TransferState State { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes       { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100.0 : 0;
    public DateTime StartedUtc   { get; set; }
    public string? ErrorMessage  { get; set; }
}
