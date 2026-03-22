namespace SyncIt.Core.Services;

public enum OverlaySyncState
{
    Unknown,
    Synced,
    Syncing,
    Pending,
    Error,
}

public interface IOverlayStateStore : IDisposable
{
    /// <param name="isSyncedFilter">
    /// Optional filter: given a relative path, returns true if the item is actively synced.
    /// When provided, only matching items get green checkmarks on startup.
    /// When null, all items are marked as synced.
    /// </param>
    Task InitializeAsync(Func<string, bool>? isSyncedFilter = null, CancellationToken ct = default);
    Task SetStateAsync(string absolutePath, OverlaySyncState state, CancellationToken ct = default);
    Task SetBatchAsync(IEnumerable<(string path, OverlaySyncState state)> items, CancellationToken ct = default);
    Task RemoveAsync(string absolutePath, CancellationToken ct = default);
    Task ClearPairAsync(string localRootPath, CancellationToken ct = default);
}
