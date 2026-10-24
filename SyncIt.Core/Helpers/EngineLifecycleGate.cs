namespace SyncIt.Core.Helpers;

/// <summary>
/// Serializes sync-engine lifecycle operations (start, stop, restart, account switch, disconnect).
/// Without it, an account switch and a config-file reload could both start engines for the same
/// pair at the same time, which produced two engines (and duplicate uploads) for one folder.
/// </summary>
public sealed class EngineLifecycleGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
