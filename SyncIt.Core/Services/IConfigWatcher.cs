using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

public interface IConfigWatcher : IDisposable
{
    /// <summary>Currently loaded config (reloaded on file change).</summary>
    SyncConfig Current { get; }

    /// <summary>Raised when the config file changes and is successfully reloaded.</summary>
    event Action<SyncConfig> ConfigReloaded;

    /// <summary>Start watching the config file for changes.</summary>
    void Start();
}
