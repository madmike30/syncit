using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Ipc;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.App.Forms;

namespace SyncIt.App;

/// <summary>
/// Unified single-process SyncIt application — runs like OneDrive / Google Drive.
/// Sync engines run on background threads, tray icon on the WinForms UI thread,
/// direct in-memory communication (no IPC, no child processes).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SyncItApp : ApplicationContext
{
    internal static readonly string ConfigPath =
        PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SyncItApp> _logger;
    private readonly IconManager _iconManager;
    private readonly TraySettings _traySettings;

    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _menu = null!;

    // Menu items updated dynamically
    private ToolStripMenuItem _accountItem = null!;
    private ToolStripMenuItem _storageItem = null!;
    private ToolStripMenuItem _activityItem1 = null!;
    private ToolStripMenuItem _activityItem2 = null!;
    private ToolStripMenuItem _activityItem3 = null!;
    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _autoStartItem = null!;
    private ToolStripMenuItem _notificationsItem = null!;

    // ── Sync engine state (in-process, no IPC) ─────────────────────────────────
    private SyncConfig _config = new();
    private CancellationTokenSource _engineCts = new();
    private readonly List<SyncEngine> _engines = new();
    private readonly List<DeletionCoordinator> _deletionCoordinators = new();
    private IConfigWatcher? _configWatcher;
    private IGoogleDriveService? _driveService;
    private IOverlayStateStore? _overlayStore;
    private IActivityLog? _activityLog;

    private bool _globalPaused;
    private SyncStatus? _lastStatus;
    private List<FileTransferProgress>? _activeTransfers;
    private string? _pendingFreezeForPair;
    private readonly List<string> _recentActivity = new();
    private DateTime _lastTransferPush = DateTime.MinValue;

    // Account info cache
    private DriveAccountInfo? _accountInfo;
    private DateTime _lastAccountFetch = DateTime.MinValue;

    public SyncItApp(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger        = loggerFactory.CreateLogger<SyncItApp>();
        _iconManager   = new IconManager();
        _traySettings  = TraySettings.Load();

        InitializeNotifyIcon();

        // Start sync engine on a background thread
        _ = Task.Run(StartSyncEngineAsync);
    }

    // =========================================================================
    // Sync engine lifecycle (runs on background threads)
    // =========================================================================

    private async Task StartSyncEngineAsync()
    {
        var ct = _engineCts.Token;
        try
        {
            _logger.LogInformation("SyncIt starting...");

            // Load config
            if (!File.Exists(ConfigPath))
            {
                _logger.LogWarning("No config file found at {Path}. Run first-run setup.", ConfigPath);
                InvokeUI(() => SetTooltip("SyncIt — No config. Right-click for setup."));
                return;
            }

            _config = JsonSerializer.Deserialize<SyncConfig>(
                await File.ReadAllTextAsync(ConfigPath, ct), JsonOpts)
                ?? throw new InvalidOperationException("Failed to parse syncit.json");

            // Initialize databases
            var stateStore = CreateStateStore();
            await stateStore.InitializeAsync(ct);
            var journal = CreateDeletionJournal();
            await journal.InitializeAsync(ct);

            // OAuth
            var oauthService = new OAuthService(_config, CreateLogger<OAuthService>());
            var credential   = await oauthService.InitializeAsync(ct);

            // Shared services
            var driveService = new GoogleDriveService(credential, CreateLogger<GoogleDriveService>(), _config);
            _driveService = driveService;
            var globFilter   = new GlobFilter();
            var atomicWriter = new AtomicFileWriter(CreateLogger<AtomicFileWriter>());

            // Activity log
            _activityLog = new ActivityLog(_config.ActivityLogPath, CreateLogger<ActivityLog>());
            await _activityLog.InitializeAsync(ct);
            await _activityLog.PruneAsync(_config.ActivityLogRetentionDays, ct);

            // Explorer overlay
            var firstPair = _config.SyncPairs.FirstOrDefault(p => p.Enabled);
            if (firstPair is not null)
            {
                var localPath = PathHelper.Expand(firstPair.LocalPath);
                _overlayStore = new CloudSyncProvider(localPath, "SyncIt", CreateLogger<CloudSyncProvider>());
                await _overlayStore.InitializeAsync(ct);
            }

            // Start engines
            await StartEnginesAsync(driveService, globFilter, atomicWriter, ct);

            // Config hot-reload
            _configWatcher = new ConfigWatcher(ConfigPath, _config, CreateLogger<ConfigWatcher>());
            _configWatcher.ConfigReloaded += newConfig =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Config reloaded — restarting engines...");
                        await StopEnginesAsync();
                        _config = newConfig;
                        await StartEnginesAsync(driveService, globFilter, atomicWriter, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to restart engines after config reload.");
                    }
                });
            };
            _configWatcher.Start();

            // Status push loop (updates tray every 10s)
            await StatusPushLoopAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "SyncIt engine fatal error.");
            InvokeUI(() =>
            {
                _notifyIcon.Icon = _iconManager.ErrorIcon;
                SetTooltip("SyncIt — Fatal error");
            });
        }
        finally
        {
            await StopEnginesAsync();
            _overlayStore?.Dispose();
        }
    }

    private async Task StartEnginesAsync(
        GoogleDriveService driveService,
        GlobFilter globFilter,
        AtomicFileWriter atomicWriter,
        CancellationToken ct)
    {
        foreach (var pair in _config.SyncPairs.Where(p => p.Enabled))
        {
            var stateStore = CreateStateStore();
            await stateStore.InitializeAsync(ct);

            var deletionJournal = CreateDeletionJournal();
            await deletionJournal.InitializeAsync(ct);

            var trashManager    = new LocalTrashManager(_config.LocalTrashPath, CreateLogger<LocalTrashManager>());
            var safetyValidator = new DeletionSafetyValidator(_config, stateStore, deletionJournal, CreateLogger<DeletionSafetyValidator>());
            var deletionCoord   = new DeletionCoordinator(
                _config, safetyValidator, deletionJournal, trashManager,
                driveService, stateStore, atomicWriter, CreateLogger<DeletionCoordinator>());

            deletionCoord.DeletionFreezeTriggered += OnDeletionFreezeTriggered;
            _deletionCoordinators.Add(deletionCoord);

            var changeQueue   = new ChangeQueue();
            var conflictRes   = new ConflictResolver(CreateLogger<ConflictResolver>());
            var retryTracker  = new FailedOperationTracker(_config, CreateLogger<FailedOperationTracker>());

            var engine = new SyncEngine(
                pair, _config, driveService, stateStore, conflictRes,
                deletionCoord, globFilter, changeQueue, safetyValidator,
                atomicWriter, CreateLogger<SyncEngine>(), _overlayStore, _activityLog, retryTracker);

            engine.StatusChanged += _ => OnEngineStatusChanged();
            engine.TransferTracker.ProgressChanged += _ => ThrottledTransferPush();

            _engines.Add(engine);
            await engine.StartAsync(pair, ct);
        }

        _logger.LogInformation("Started {Count} sync engine(s).", _engines.Count);
    }

    private async Task StopEnginesAsync()
    {
        foreach (var engine in _engines)
        {
            try { await engine.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping engine."); }
        }
        _engines.Clear();
        _deletionCoordinators.Clear();
    }

    // =========================================================================
    // Status push loop (background → UI thread)
    // =========================================================================

    private async Task StatusPushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            // Refresh account info every 5 minutes
            if (_driveService is not null &&
                (DateTime.UtcNow - _lastAccountFetch).TotalMinutes >= 5)
            {
                try
                {
                    _accountInfo = await _driveService.GetAccountInfoAsync(ct);
                    _lastAccountFetch = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to refresh account info.");
                }
            }

            PushStatus();
        }
    }

    private void OnEngineStatusChanged() => PushStatus();

    private void PushStatus()
    {
        var status = BuildStatus();
        _lastStatus = status;
        InvokeUI(() => UpdateUI(status));
    }

    private SyncStatus BuildStatus()
    {
        var status = new SyncStatus
        {
            State                 = _globalPaused ? SyncState.Paused : SyncState.Idle,
            LastSuccessfulSyncUtc = null,
            PairStatuses          = _engines.Select(e => e.GetStatus()).ToList(),
        };

        if (_accountInfo is not null)
        {
            status.AccountEmail      = _accountInfo.Email;
            status.StorageUsedBytes  = _accountInfo.StorageUsedBytes;
            status.StorageTotalBytes = _accountInfo.StorageTotalBytes;
        }

        if (_engines.Count > 0)
        {
            status.Metrics = new SyncMetricsSnapshot
            {
                TotalBytesUploaded   = _engines.Sum(e => e.Metrics.TotalBytesUploaded),
                TotalBytesDownloaded = _engines.Sum(e => e.Metrics.TotalBytesDownloaded),
                TotalApiCalls        = _engines.Sum(e => e.Metrics.TotalApiCalls),
                TotalSyncCycles      = _engines.Sum(e => e.Metrics.TotalSyncCycles),
                TotalErrors          = _engines.Sum(e => e.Metrics.TotalErrors),
                LastCycleDurationMs  = _engines.Max(e => e.Metrics.LastCycleDurationMs),
            };
        }

        return status;
    }

    // =========================================================================
    // Tray icon setup
    // =========================================================================

    private void InitializeNotifyIcon()
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false };

        _accountItem = new ToolStripMenuItem("SyncIt") { Enabled = false };
        _accountItem.Font = new Font(_accountItem.Font, FontStyle.Bold);
        _menu.Items.Add(_accountItem);

        _storageItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _menu.Items.Add(_storageItem);
        _menu.Items.Add(new ToolStripSeparator());

        _activityItem1 = new ToolStripMenuItem("No recent activity") { Enabled = false };
        _activityItem2 = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _activityItem3 = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _menu.Items.Add(_activityItem1);
        _menu.Items.Add(_activityItem2);
        _menu.Items.Add(_activityItem3);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Open Sync Folder",    null, OnOpenSyncFolder);
        _menu.Items.Add("Open Status...",       null, OnOpenStatus);
        _menu.Items.Add("Activity History...",  null, OnOpenActivityHistory);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Sync Now",            null, OnSyncNow);
        _pauseItem = new ToolStripMenuItem("Pause Syncing", null, OnPauseResume);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add("Select Folders...",   null, OnSelectFolders);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Preferences...",      null, OnOpenPreferences);
        _menu.Items.Add("Open Log Folder",     null, OnOpenLogFolder);

        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStartHelper.IsEnabled,
        };
        _menu.Items.Add(_autoStartItem);

        _notificationsItem = new ToolStripMenuItem("Notifications", null, OnToggleNotifications)
        {
            Checked = _traySettings.NotificationsEnabled,
        };
        _menu.Items.Add(_notificationsItem);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Quit SyncIt", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Text             = "SyncIt — Starting...",
            Icon             = _iconManager.IdleIcon,
            ContextMenuStrip = _menu,
            Visible          = true,
        };

        _notifyIcon.DoubleClick += OnOpenStatus;
    }

    // =========================================================================
    // UI updates (always runs on UI thread)
    // =========================================================================

    private void UpdateUI(SyncStatus status)
    {
        _notifyIcon.Icon = status.State switch
        {
            SyncState.Syncing        => _iconManager.SyncingIcon,
            SyncState.Paused         => _iconManager.PausedIcon,
            SyncState.Error          => _iconManager.ErrorIcon,
            SyncState.SafetyHalt     => _iconManager.ErrorIcon,
            SyncState.DeletionFrozen => _iconManager.WarningIcon,
            _                        => _iconManager.IdleIcon,
        };

        SetTooltip(BuildTooltip(status));

        if (status.AccountEmail is not null)
            _accountItem.Text = $"SyncIt — {status.AccountEmail}";

        UpdateStorageDisplay(status);
        _pauseItem.Text = status.State == SyncState.Paused ? "Resume Syncing" : "Pause Syncing";
        UpdateRecentActivity(status);
    }

    private void UpdateStorageDisplay(SyncStatus status)
    {
        if (status.StorageTotalBytes > 0)
        {
            var used  = FormatBytes(status.StorageUsedBytes);
            var total = FormatBytes(status.StorageTotalBytes);
            var pct   = (double)status.StorageUsedBytes / status.StorageTotalBytes;
            var bar   = BuildProgressBar(pct, 10);
            _storageItem.Text = $"{bar}  {used} / {total}";
        }
        else if (status.AccountEmail is not null)
        {
            _storageItem.Text = status.State switch
            {
                SyncState.Idle    => "Up to date",
                SyncState.Syncing => "Syncing...",
                SyncState.Paused  => "Paused",
                SyncState.Error   => "Error",
                _ => status.State.ToString(),
            };
        }
    }

    private void UpdateRecentActivity(SyncStatus status)
    {
        var items = new List<string>();

        if (_activeTransfers is not null)
            foreach (var t in _activeTransfers.Take(3))
            {
                var dir  = t.Direction == TransferDirection.Upload ? "\u2191" : "\u2193";
                items.Add($"{dir} {Path.GetFileName(t.RelativePath)} — {t.ProgressPercent:F0}%");
            }

        foreach (var pair in status.PairStatuses)
            if (pair.State == SyncState.Idle && pair.PendingChanges == 0)
            {
                var entry = $"\u2713 {pair.RemoteFolderName} — up to date";
                if (_recentActivity.Count == 0 || _recentActivity[0] != entry)
                {
                    _recentActivity.Insert(0, entry);
                    if (_recentActivity.Count > 3) _recentActivity.RemoveAt(3);
                }
            }

        foreach (var r in _recentActivity)
            if (items.Count < 3) items.Add(r);

        SetActivityItem(_activityItem1, items.Count > 0 ? items[0] : "No recent activity", items.Count > 0);
        SetActivityItem(_activityItem2, items.Count > 1 ? items[1] : "", items.Count > 1);
        SetActivityItem(_activityItem3, items.Count > 2 ? items[2] : "", items.Count > 2);
    }

    // =========================================================================
    // Menu handlers — direct engine calls (no IPC)
    // =========================================================================

    private void OnOpenSyncFolder(object? sender, EventArgs e)
    {
        var localPath = _config.SyncPairs.FirstOrDefault(p => p.Enabled)?.LocalPath;
        if (localPath is not null)
        {
            localPath = PathHelper.Expand(localPath);
            if (Directory.Exists(localPath)) { Process.Start("explorer.exe", localPath); return; }
        }
        MessageBox.Show("No sync folder configured.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnOpenStatus(object? sender, EventArgs e)
    {
        using var form = new StatusForm(_lastStatus, _activeTransfers);
        form.ShowDialog();
    }

    private void OnOpenActivityHistory(object? sender, EventArgs e)
    {
        using var form = new ActivityHistoryForm(async () =>
        {
            if (_activityLog is null) return null;
            return await _activityLog.GetRecentAsync(200);
        });
        form.ShowDialog();
    }

    private void OnSyncNow(object? sender, EventArgs e)
    {
        foreach (var engine in _engines)
            _ = Task.Run(() => engine.TriggerSyncAsync(_engineCts.Token));
    }

    private void OnPauseResume(object? sender, EventArgs e)
    {
        _globalPaused = !_globalPaused;
        foreach (var engine in _engines)
        {
            if (_globalPaused) engine.Pause();
            else engine.Resume();
        }
        PushStatus();
    }

    private void OnSelectFolders(object? sender, EventArgs e)
    {
        var pairs = _config.SyncPairs;
        if (pairs.Count == 0)
        {
            MessageBox.Show("No sync pairs configured.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SyncPair? target = pairs.Count == 1
            ? pairs[0]
            : PickPair(pairs);
        if (target is null) return;

        // Get subfolders directly from drive service (no IPC round-trip)
        var tcs = new TaskCompletionSource<SubfoldersResultPayload?>();
        _ = Task.Run(async () =>
        {
            try
            {
                if (_driveService is null) { tcs.SetResult(null); return; }
                var subfolders = await _driveService.ListSubfoldersAsync(target.RemoteFolderId, ct: _engineCts.Token);
                tcs.SetResult(new SubfoldersResultPayload
                {
                    SyncPairId = target.Id,
                    Subfolders = subfolders.Select(s => new SubfolderEntry
                    {
                        Name = s.Name,
                        RelativePath = s.RelativePath,
                        DriveId = s.DriveId,
                    }).ToList(),
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new SubfoldersResultPayload
                {
                    SyncPairId = target.Id,
                    Error = ex.Message,
                });
            }
        });

        using var form = new SelectFoldersForm(target, ConfigPath, tcs.Task);
        form.ShowDialog();
    }

    private static SyncPair? PickPair(List<SyncPair> pairs)
    {
        using var picker = new PairPickerDialog(pairs);
        return picker.ShowDialog() == DialogResult.OK ? picker.SelectedPair : null;
    }

    private void OnOpenPreferences(object? sender, EventArgs e)
    {
        if (File.Exists(ConfigPath))
            Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });
        else
            MessageBox.Show($"Config not found:\n{ConfigPath}", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        var logDir = PathHelper.Expand(@"%APPDATA%\SyncIt\logs");
        if (Directory.Exists(logDir))
            Process.Start("explorer.exe", logDir);
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStartHelper.SetEnabled(!AutoStartHelper.IsEnabled);
        _autoStartItem.Checked = AutoStartHelper.IsEnabled;
    }

    private void OnToggleNotifications(object? sender, EventArgs e)
    {
        _traySettings.NotificationsEnabled = !_traySettings.NotificationsEnabled;
        _traySettings.Save();
        _notificationsItem.Checked = _traySettings.NotificationsEnabled;
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _engineCts.Cancel();
        Application.Exit();
    }

    // =========================================================================
    // Deletion freeze (engine event → UI)
    // =========================================================================

    private void OnDeletionFreezeTriggered(string pairId, IReadOnlyList<DeletionRecord> records)
    {
        _pendingFreezeForPair = pairId;
        InvokeUI(() =>
        {
            _notifyIcon.Icon = _iconManager.WarningIcon;
            ShowBalloon("SyncIt — Deletion Safety",
                $"{records.Count} files would be deleted. Click to review.",
                ToolTipIcon.Warning);
            _notifyIcon.BalloonTipClicked += OnFreezeDialogRequested;
        });
    }

    private void OnFreezeDialogRequested(object? sender, EventArgs e)
    {
        _notifyIcon.BalloonTipClicked -= OnFreezeDialogRequested;
        if (_pendingFreezeForPair is null) return;

        using var form = new DeletionWarningForm(_pendingFreezeForPair, _lastStatus);
        var result = form.ShowDialog();

        var pairId = _pendingFreezeForPair;
        _pendingFreezeForPair = null;

        // Direct call to coordinators (no IPC)
        _ = Task.Run(async () =>
        {
            foreach (var coord in _deletionCoordinators)
            {
                try
                {
                    if (result == DialogResult.Yes)
                        await coord.ApproveFrozenAsync(pairId, _engineCts.Token);
                    else
                        await coord.CancelFrozenAsync(pairId, _engineCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling frozen deletions for {PairId}", pairId);
                }
            }
        });
    }

    // =========================================================================
    // Transfer progress (throttled to 1Hz)
    // =========================================================================

    private void ThrottledTransferPush()
    {
        if ((DateTime.UtcNow - _lastTransferPush).TotalSeconds < 1) return;
        _lastTransferPush = DateTime.UtcNow;
        _activeTransfers = _engines.SelectMany(e => e.TransferTracker.ActiveTransfers).ToList();
        InvokeUI(() => UpdateRecentActivity(_lastStatus ?? new SyncStatus()));
    }

    // =========================================================================
    // First-run OAuth
    // =========================================================================

    public void RunAuthOnly()
    {
        using var form = new FirstRunForm();
        form.ShowDialog();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void InvokeUI(Action action)
    {
        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
            _notifyIcon.ContextMenuStrip.Invoke(action);
        else
            action();
    }

    private void SetTooltip(string text) =>
        _notifyIcon.Text = text[..Math.Min(text.Length, 63)];

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (!_traySettings.NotificationsEnabled) return;
        _notifyIcon.ShowBalloonTip(5000, title, text, icon);
    }

    private static string BuildTooltip(SyncStatus status)
    {
        var state = status.State switch
        {
            SyncState.Idle           => "Up to date",
            SyncState.Syncing        => "Syncing...",
            SyncState.Paused         => "Paused",
            SyncState.Error          => "Error",
            SyncState.SafetyHalt     => "Safety Halt",
            SyncState.DeletionFrozen => "Deletion Frozen",
            _ => status.State.ToString(),
        };
        var tip = status.AccountEmail is not null
            ? $"SyncIt — {status.AccountEmail}\n{state}"
            : $"SyncIt — {state}";
        return tip;
    }

    private static void SetActivityItem(ToolStripMenuItem item, string text, bool visible)
    {
        item.Text = text;
        item.Visible = visible || text == "No recent activity";
    }

    private static string BuildProgressBar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int filled = (int)(fraction * width);
        return new string('\u2588', filled) + new string('\u2591', width - filled);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private IFileStateStore CreateStateStore() =>
        new FileStateStore(_config.StateDbPath, CreateLogger<FileStateStore>());

    private DeletionJournal CreateDeletionJournal() =>
        new DeletionJournal(_config.DeleteJournalPath, CreateLogger<DeletionJournal>());

    private ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engineCts.Cancel();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _iconManager.Dispose();
            _engineCts.Dispose();
        }
        base.Dispose(disposing);
    }
}
