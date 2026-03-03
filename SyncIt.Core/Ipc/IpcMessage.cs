using System.Text.Json.Serialization;
using SyncIt.Core.Models;

namespace SyncIt.Core.Ipc;

public enum IpcMessageType
{
    // Service → Tray (push)
    StatusUpdate,              // Payload: SyncStatus
    ErrorOccurred,             // Payload: ErrorPayload
    AuthRequired,              // No payload — token expired, tray should trigger browser
    DeletionFreezeTriggered,   // Payload: DeletionFreezePayload
    SubfoldersResult,          // Payload: SubfoldersResultPayload — response to GetSubfolders
    FileTransferUpdate,        // Payload: FileTransferUpdatePayload — per-file progress

    // Tray → Service (commands)
    Pause,                     // No payload
    Resume,                    // No payload
    RequestStatus,             // No payload — service responds with StatusUpdate
    TriggerSync,               // Payload: TriggerSyncPayload (pairId or null = all)
    ApproveDeletions,          // Payload: pairId string
    CancelDeletions,           // Payload: pairId string
    GetSubfolders,             // Payload: GetSubfoldersPayload — requests subfolder list for a pair
    Shutdown,                  // No payload — graceful stop

    // Bidirectional responses
    Ack,                       // Payload: AckPayload
    Nack,                      // Payload: NackPayload
}

/// <summary>
/// Single IPC frame. Sent as newline-delimited JSON over the named pipe.
/// </summary>
public class IpcMessage
{
    public IpcMessageType Type    { get; set; }
    public string?        Payload { get; set; }  // JSON-serialized payload, type-specific
    public DateTime       SentUtc { get; set; } = DateTime.UtcNow;
}

// -------------------------------------------------------------------------
// Payload types
// -------------------------------------------------------------------------

public class ErrorPayload
{
    public string  SyncPairId { get; set; } = string.Empty;
    public string  Message    { get; set; } = string.Empty;
    public string? StackTrace  { get; set; }
    public ErrorSeverity Severity { get; set; }
}

public enum ErrorSeverity { Warning, Error, Fatal }

public class DeletionFreezePayload
{
    public string   SyncPairId       { get; set; } = string.Empty;
    public string   Reason           { get; set; } = string.Empty;
    public int      Count            { get; set; }
    public List<string> FilePaths    { get; set; } = new();
    public List<long>   FileSizes    { get; set; } = new();
}

public class TriggerSyncPayload
{
    /// <summary>Null = trigger all pairs. Non-null = trigger specific pair.</summary>
    public string? SyncPairId { get; set; }
}

public class AckPayload
{
    public IpcMessageType InResponseTo { get; set; }
}

public class NackPayload
{
    public IpcMessageType InResponseTo { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class GetSubfoldersPayload
{
    public string SyncPairId { get; set; } = string.Empty;
}

/// <summary>
/// Sent by the service in response to GetSubfolders. Contains the flat subfolder tree
/// the user can browse in the tray's SelectFoldersForm.
/// </summary>
public class SubfoldersResultPayload
{
    public string SyncPairId { get; set; } = string.Empty;
    public List<SubfolderEntry> Subfolders { get; set; } = new();
    /// <summary>Non-null when the service could not list subfolders (error message).</summary>
    public string? Error { get; set; }
}

public class SubfolderEntry
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Forward-slash relative path from the sync pair root, e.g. "Work/Reports".</summary>
    public string RelativePath { get; set; } = string.Empty;
    public string DriveId { get; set; } = string.Empty;
}

/// <summary>Active file transfer progress snapshot pushed to tray (throttled to 1Hz).</summary>
public class FileTransferUpdatePayload
{
    public List<FileTransferProgress> ActiveTransfers { get; set; } = new();
}

