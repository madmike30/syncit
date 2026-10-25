using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Watches syncit.json for changes and hot-reloads the config.
/// - Debounces bursts of events (partial writes, editors that save twice).
/// - Catches in-place writes, atomic replace (temp file + rename) and delete/recreate.
/// - Retries while the writer still holds the file locked.
/// - Compares a content hash: touches that change nothing are ignored, nothing is applied twice.
/// - A periodic check catches dropped OS events and edits made while the app was still starting.
/// </summary>
public sealed class ConfigWatcher : IConfigWatcher
{
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _pollInterval;
    private readonly string _configPath;
    private readonly ILogger<ConfigWatcher> _logger;
    private readonly object _timerLock = new();
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private Timer? _pollTimer;
    private string? _lastHash;
    private volatile bool _disposed;

    public SyncConfig Current { get; private set; }

    public event Action<SyncConfig>? ConfigReloaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Create this right after the config is loaded: the content hash taken here is the baseline, so an
    /// edit made while the app is still starting is applied as soon as <see cref="Start"/> runs.
    /// </summary>
    public ConfigWatcher(string configPath, SyncConfig initialConfig, ILogger<ConfigWatcher> logger,
        TimeSpan? debounceDelay = null, TimeSpan? pollInterval = null)
    {
        _configPath    = PathHelper.Expand(configPath);
        Current        = initialConfig;
        _logger        = logger;
        _debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(1);
        _pollInterval  = pollInterval ?? TimeSpan.FromSeconds(30);
        _lastHash      = TryHash(_configPath);
    }

    public void Start()
    {
        var dir  = Path.GetDirectoryName(_configPath)!;
        var file = Path.GetFileName(_configPath);

        _fsw = new FileSystemWatcher(dir, file)
        {
            NotifyFilter       = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Changed += (_, _) => Schedule();
        _fsw.Created += (_, _) => Schedule();
        _fsw.Renamed += (_, _) => Schedule();
        _fsw.Error   += (_, e) => { _logger.LogDebug(e.GetException(), "Config watcher error; re-checking."); Schedule(); };
        _fsw.EnableRaisingEvents = true;

        _pollTimer = new Timer(_ => TryReload(), null, _pollInterval, _pollInterval);
        _logger.LogInformation("Config watcher started on '{Path}'", _configPath);

        Schedule(); // pick up edits made between load and Start
    }

    private void Schedule()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            if (_debounceTimer is null)
                _debounceTimer = new Timer(_ => TryReload(), null, _debounceDelay, Timeout.InfiniteTimeSpan);
            else
                _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the file (retrying while locked) and raises <see cref="ConfigReloaded"/> if its content changed.</summary>
    internal bool TryReload()
    {
        if (_disposed) return false;
        lock (_reloadLock)
        {
            string? json = null;
            for (var attempt = 0; attempt < 6 && json is null && !_disposed; attempt++)
            {
                try { json = ReadShared(_configPath); }
                catch (FileNotFoundException) { return false; }      // mid atomic replace; Created/Renamed follows
                catch (DirectoryNotFoundException) { return false; }
                catch (IOException) { Thread.Sleep(200 * (attempt + 1)); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200 * (attempt + 1)); }
            }
            if (json is null)
            {
                if (!_disposed) _logger.LogWarning("Config file stayed locked; will retry on the next check.");
                return false;
            }

            var hash = Hash(json);
            if (hash == _lastHash) return false;

            try
            {
                var config = JsonSerializer.Deserialize<SyncConfig>(json, JsonOptions);
                if (config is null) return false;
                _lastHash = hash;
                Current = config;
                _logger.LogInformation("Config reloaded from '{Path}'", _configPath);
                ConfigReloaded?.Invoke(config);
                return true;
            }
            catch (Exception ex)
            {
                // Hash not recorded: a half-written file gets another try once the write completes.
                _logger.LogWarning(ex, "Failed to reload config — keeping previous config.");
                return false;
            }
        }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string? TryHash(string path)
    {
        try { return Hash(ReadShared(path)); } catch { return null; }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _debounceTimer?.Dispose();
        }
        _fsw?.Dispose();
        _pollTimer?.Dispose();
    }
}
