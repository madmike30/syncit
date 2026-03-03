using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

public interface IChangeQueue
{
    /// <summary>
    /// Enqueue a change event. Events for the same path are debounced:
    /// multiple rapid events collapse into one after a quiet period.
    /// </summary>
    void Enqueue(ChangeEvent changeEvent);

    /// <summary>
    /// Stream change events as they become ready (after debounce).
    /// This is an infinite async sequence — consume until cancellation.
    /// </summary>
    IAsyncEnumerable<ChangeEvent> ReadAllAsync(CancellationToken ct);

    /// <summary>Number of events currently pending (waiting in debounce).</summary>
    int PendingCount { get; }

    /// <summary>Total events ready and waiting to be consumed.</summary>
    int ReadyCount { get; }
}
