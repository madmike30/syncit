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
    Task InitializeAsync(CancellationToken ct = default);
    Task SetStateAsync(string absolutePath, OverlaySyncState state, CancellationToken ct = default);
    Task SetBatchAsync(IEnumerable<(string path, OverlaySyncState state)> items, CancellationToken ct = default);
    Task RemoveAsync(string absolutePath, CancellationToken ct = default);
    Task ClearPairAsync(string localRootPath, CancellationToken ct = default);
}
