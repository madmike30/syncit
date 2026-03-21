using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Watches syncit.json for changes and hot-reloads the config.
/// Debounces file change events by 2 seconds to avoid reloading on partial writes.
/// </summary>
public sealed class ConfigWatcher : IConfigWatcher
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly string _configPath;
    private readonly ILogger<ConfigWatcher> _logger;
    private readonly object _timerLock = new();
    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;

    public SyncConfig Current { get; private set; }

    public event Action<SyncConfig>? ConfigReloaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ConfigWatcher(string configPath, SyncConfig initialConfig, ILogger<ConfigWatcher> logger)
    {
        _configPath = PathHelper.Expand(configPath);
        Current     = initialConfig;
        _logger     = logger;
    }

    public void Start()
    {
        var dir  = Path.GetDirectoryName(_configPath)!;
        var file = Path.GetFileName(_configPath);

        _fsw = new FileSystemWatcher(dir, file)
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += OnChanged;
        _logger.LogInformation("Config watcher started on '{Path}'", _configPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => TryReload(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void TryReload()
    {
        try
        {
            var json   = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<SyncConfig>(json, JsonOptions);
            if (config is null) return;

            Current = config;
            _logger.LogInformation("Config reloaded from '{Path}'", _configPath);
            ConfigReloaded?.Invoke(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload config — keeping previous config.");
        }
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _debounceTimer?.Dispose();
    }
}
