using System.Text.Json.Serialization;

namespace SyncIt.Core.Models;

/// <summary>
/// Root configuration object deserialized from %APPDATA%\SyncIt\syncit.json.
/// clientId and clientSecret are NOT stored here — they live in Windows Credential Manager.
/// </summary>
public class SyncConfig
{
    public string TokenStorePath    { get; set; } = @"%APPDATA%\SyncIt\token.bin";
    public string StateDbPath       { get; set; } = @"%APPDATA%\SyncIt\state.db";
    public string DeleteJournalPath { get; set; } = @"%APPDATA%\SyncIt\delete-journal.db";
    public string LogDirectory      { get; set; } = @"%APPDATA%\SyncIt\logs";
    public string LocalTrashPath    { get; set; } = @"%APPDATA%\SyncIt\.syncit-trash";

    /// <summary>How often (in seconds) to poll Google Drive for remote changes.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// If a single sync cycle would delete more than this many files, freeze and require user approval.
    /// Default 10 — prevents catastrophic mass-delete from misconfiguration.
    /// </summary>
    public int MaxDeletionsPerCycle { get; set; } = 10;

    /// <summary>
    /// Number of sync cycles a deletion must wait before executing (grace period).
    /// Default 2 — gives user time to catch accidental deletes before they propagate.
    /// </summary>
    public int DeletionGraceCycles { get; set; } = 2;

    /// <summary>Name of the named pipe used for tray↔service IPC.</summary>
    public string PipeName { get; set; } = "SyncItIpc";

    public List<SyncPair> SyncPairs { get; set; } = new();
}
