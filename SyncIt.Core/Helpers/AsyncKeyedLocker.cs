using System.Collections.Concurrent;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Serializes async work per logical key while allowing different keys to proceed concurrently.
/// </summary>
public sealed class AsyncKeyedLocker
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;

    public AsyncKeyedLocker()
        : this(StringComparer.Ordinal)
    {
    }

    public AsyncKeyedLocker(IEqualityComparer<string> comparer)
    {
        _locks = new ConcurrentDictionary<string, SemaphoreSlim>(comparer);
    }

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(action);

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
