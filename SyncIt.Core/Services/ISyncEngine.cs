using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

public interface ISyncEngine : IAsyncDisposable
{
    /// <summary>
    /// Start monitoring and syncing a pair.
    /// Sets up FileSystemWatcher, starts the polling loop, runs initial sync if no baseline.
    /// </summary>
    Task StartAsync(SyncPair pair, CancellationToken ct);

    /// <summary>Current sync status for this engine's pair.</summary>
    SyncPairStatus GetStatus();

    /// <summary>Pause sync (stops processing the change queue).</summary>
    void Pause();

    /// <summary>Resume a paused sync.</summary>
    void Resume();

    /// <summary>Force an immediate sync cycle.</summary>
    Task TriggerSyncAsync(CancellationToken ct);

    /// <summary>Raised when the pair's status changes (forwarded to IPC).</summary>
    event Action<SyncPairStatus> StatusChanged;
}
