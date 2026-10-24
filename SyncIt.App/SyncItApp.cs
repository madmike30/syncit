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

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

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
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _autoStartItem = null!;

    // Dashboard (kept alive so it can be shown/hidden)
    private DashboardForm? _dashboard;

    // ── Sync engine state (in-process, no IPC) ─────────────────────────────────
    private SyncConfig _config = new();
    private CancellationTokenSource _engineCts = new();
    private readonly List<SyncEngine> _engines = new();
    private readonly List<DeletionCoordinator> _deletionCoordinators = new();
    private IConfigWatcher? _configWatcher;
    private IGoogleDriveService? _driveService;
    private IOverlayStateStore? _overlayStore;
    private IActivityLog? _activityLog;

    private readonly object _enginesLock = new();
    private readonly EngineLifecycleGate _lifecycle = new();
    private volatile bool _suppressConfigReload;
    private OAuthService? _oauthService;
    private bool _globalPaused;
    private SyncStatus? _lastStatus;
    private List<FileTransferProgress>? _activeTransfers;
    private readonly List<string> _recentActivity = new();
    private DateTime _lastTransferPush = DateTime.MinValue;

    // Session start time — used to filter activity list to current session
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow;

    // Account info cache
    private DriveAccountInfo? _accountInfo;
    private DateTime _lastAccountFetch = DateTime.MinValue;

    public SyncItApp(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger        = loggerFactory.CreateLogger<SyncItApp>();
        _iconManager   = new IconManager();
        _traySettings  = TraySettings.Load();
        Theme.Initialize(_traySettings.Theme);

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
                _logger.LogInformation("No config file found at {Path}. Creating a default one.", ConfigPath);
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(new SyncConfig(), WriteOpts), ct);
            }

            _config = JsonSerializer.Deserialize<SyncConfig>(
                await File.ReadAllTextAsync(ConfigPath, ct), JsonOpts)
                ?? throw new InvalidOperationException("Failed to parse syncit.json");

            // Migrate: ensure all pairs use Drive root ("root") as their remote base
            foreach (var pair in _config.SyncPairs)
            {
                if (pair.RemoteFolderId != "root" && !string.IsNullOrEmpty(pair.RemoteFolderId))
                {
                    _logger.LogInformation(
                        "Migrating pair '{Id}' from folder '{OldFolder}' to Drive root. " +
                        "Previous folder will be added to SelectedSubfolders.",
                        pair.Id, pair.RemoteFolderName);

                    // Add the old folder name as a selected subfolder (if not already present)
                    var oldName = pair.RemoteFolderName.Replace("My Drive / ", "").Trim();
                    if (!string.IsNullOrEmpty(oldName) && !pair.SelectedSubfolders.Contains(oldName, StringComparer.OrdinalIgnoreCase))
                        pair.SelectedSubfolders.Add(oldName);

                    pair.RemoteFolderId   = "root";
                    pair.RemoteFolderName = "My Drive";

                    // Save migrated config
                    var writeOpts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented               = true,
                        Converters                  = { new JsonStringEnumConverter() },
                    };
                    await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(_config, writeOpts), ct);

                    // Reset state DBs since the remote root changed
                    foreach (var db in new[] { _config.StateDbPath, _config.DeleteJournalPath })
                    {
                        var path = PathHelper.Expand(db);
                        foreach (var ext in new[] { "", "-shm", "-wal" })
                        {
                            var f = path + ext;
                            try { if (File.Exists(f)) File.Delete(f); }
                            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete {File}", f); }
                        }
                    }
                }
            }

            // Not signed in yet: show the friendly sign-in window instead of silently opening a browser.
            if (!CredentialHelper.TokenExists(PathHelper.Expand(_config.TokenStorePath)))
            {
                var signedIn = false;
                InvokeUI(() => signedIn = ShowSignIn());
                if (!signedIn)
                {
                    _logger.LogInformation("Not signed in. Waiting for the user to sign in from the tray menu.");
                    InvokeUI(() => SetTooltip("SyncIt — Not signed in"));
                    return;
                }
            }

            // OAuth
            var oauthService = new OAuthService(_config, CreateLogger<OAuthService>());
            _oauthService = oauthService;
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

            // Prune local trash older than retention period
            var trashManager = new LocalTrashManager(_config.LocalTrashPath, CreateLogger<LocalTrashManager>());
            trashManager.PruneOldEntries();

            // Explorer overlay
            var firstPair = _config.SyncPairs.FirstOrDefault(p => p.Enabled);
            if (firstPair is not null)
            {
                var localPath = PathHelper.Expand(firstPair.LocalPath);
                _overlayStore = new CloudSyncProvider(localPath, "SyncIt", CreateLogger<CloudSyncProvider>());
                await _overlayStore.InitializeAsync(
                    isSyncedFilter: path => globFilter.IsIncluded(path, firstPair), ct);
            }

            try
            {
                _accountInfo = await driveService.GetAccountInfoAsync(ct);
                _lastAccountFetch = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch initial account info.");
            }

            // Start engines
            await StartEnginesAsync(driveService, globFilter, atomicWriter, ct);

            // Config hot-reload
            _configWatcher = new ConfigWatcher(ConfigPath, _config, CreateLogger<ConfigWatcher>());
            _configWatcher.ConfigReloaded += newConfig =>
            {
                // Account switch / disconnect restart engines themselves; a second restart from the
                // file watcher is what created two engines for one folder in 1.1.0.
                if (_suppressConfigReload) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Config reloaded — restarting engines...");
                        await StopEnginesAsync();

                        var oldConfig = _config;
                        _config = newConfig;
                        var liveCt = _engineCts.Token;

                        // If the Drive folder changed, reset state DBs (old file IDs are invalid)
                        var oldPair = oldConfig.SyncPairs.FirstOrDefault(p => p.Enabled);
                        var newPair = newConfig.SyncPairs.FirstOrDefault(p => p.Enabled);
                        if (oldPair is not null && newPair is not null &&
                            !string.Equals(oldPair.RemoteFolderId, newPair.RemoteFolderId, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("Drive folder changed — resetting state databases.");
                            foreach (var db in new[] { _config.StateDbPath, _config.DeleteJournalPath })
                            {
                                var path = PathHelper.Expand(db);
                                foreach (var ext in new[] { "", "-shm", "-wal" })
                                {
                                    var f = path + ext;
                                    if (File.Exists(f)) File.Delete(f);
                                }
                            }
                        }

                        // Recreate overlay store for the (possibly new) sync folder path
                        var reloadGlobFilter = new GlobFilter();
                        if (newPair is not null)
                        {
                            var newLocalPath = PathHelper.Expand(newPair.LocalPath);
                            _overlayStore?.Dispose();
                            _overlayStore = new CloudSyncProvider(newLocalPath, "SyncIt", CreateLogger<CloudSyncProvider>());
                            await _overlayStore.InitializeAsync(
                                isSyncedFilter: path => reloadGlobFilter.IsIncluded(path, newPair), liveCt);
                            _logger.LogInformation("Overlay store recreated for new path: {Path}", newLocalPath);
                        }

                        // Use current drive service and CTS (not stale captured locals)
                        var currentDrive = _driveService as GoogleDriveService ?? driveService;
                        await StartEnginesAsync(currentDrive, reloadGlobFilter, new AtomicFileWriter(CreateLogger<AtomicFileWriter>()), liveCt);
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

    private Task StartEnginesAsync(
        GoogleDriveService driveService,
        GlobFilter globFilter,
        AtomicFileWriter atomicWriter,
        CancellationToken ct) =>
        _lifecycle.RunAsync(async () =>
        {
            // Always start from a clean slate so engines can never pile up.
            await StopEnginesCoreAsync();
            await StartEnginesCoreAsync(driveService, globFilter, atomicWriter, ct);
        });

    private async Task StartEnginesCoreAsync(
        GoogleDriveService driveService,
        GlobFilter globFilter,
        AtomicFileWriter atomicWriter,
        CancellationToken ct)
    {
        foreach (var pair in SyncPairPlanner.EnabledUnique(_config.SyncPairs))
        {
            var stateStore = CreateStateStore();
            await stateStore.InitializeAsync(ct);

            var deletionJournal = CreateDeletionJournal();
            await deletionJournal.InitializeAsync(ct);

            var trashManager    = new LocalTrashManager(_config.LocalTrashPath, CreateLogger<LocalTrashManager>());
            var safetyValidator = new DeletionSafetyValidator(_config, stateStore, deletionJournal, CreateLogger<DeletionSafetyValidator>());
            var deletionCoord   = new DeletionCoordinator(
                _config, safetyValidator, deletionJournal, trashManager,
                driveService, stateStore, atomicWriter, CreateLogger<DeletionCoordinator>(),
                _activityLog);

            deletionCoord.DeletionFreezeTriggered += OnDeletionFreezeTriggered;
            lock (_enginesLock) { _deletionCoordinators.Add(deletionCoord); }

            var changeQueue   = new ChangeQueue();
            var conflictRes   = new ConflictResolver(CreateLogger<ConflictResolver>());
            var retryTracker  = new FailedOperationTracker(_config, CreateLogger<FailedOperationTracker>());

            var engine = new SyncEngine(
                pair, _config, driveService, stateStore, conflictRes,
                deletionCoord, globFilter, changeQueue, safetyValidator,
                atomicWriter, CreateLogger<SyncEngine>(), _overlayStore, _activityLog, retryTracker);

            engine.StatusChanged += OnEngineStatusChanged;
            engine.TransferTracker.ProgressChanged += OnTransferProgress;

            lock (_enginesLock) { _engines.Add(engine); }
            await engine.StartAsync(pair, ct);
        }

        _logger.LogInformation("Started {Count} sync engine(s).", _engines.Count);
    }

    private Task StopEnginesAsync() => _lifecycle.RunAsync(StopEnginesCoreAsync);

    private async Task StopEnginesCoreAsync()
    {
        // Snapshot under lock to avoid races with concurrent Add/reads
        SyncEngine[] engines;
        DeletionCoordinator[] coordinators;
        lock (_enginesLock)
        {
            engines = _engines.ToArray();
            coordinators = _deletionCoordinators.ToArray();
        }

        // Unsubscribe event handlers BEFORE disposing to prevent leaks
        foreach (var engine in engines)
        {
            engine.StatusChanged -= OnEngineStatusChanged;
            engine.TransferTracker.ProgressChanged -= OnTransferProgress;
        }
        foreach (var coord in coordinators)
            coord.DeletionFreezeTriggered -= OnDeletionFreezeTriggered;

        foreach (var engine in engines)
        {
            try { await engine.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping engine."); }
        }
        lock (_enginesLock)
        {
            _engines.Clear();
            _deletionCoordinators.Clear();
        }
    }

    // =========================================================================
    // Status push loop (background → UI thread)
    // =========================================================================

    private async Task StatusPushLoopAsync(CancellationToken ct)
    {
        int loopCount = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            loopCount++;

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

            // Every 5 minutes when idle: trim working set and collect Gen2
            if (loopCount % 30 == 0)
            {
                SyncEngine[] snapshot;
                lock (_enginesLock) { snapshot = _engines.ToArray(); }
                var isIdle = snapshot.All(e => e.GetStatus().State == SyncState.Idle);
                if (isIdle)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    GC.WaitForPendingFinalizers();
                }
            }
        }
    }

    private void OnEngineStatusChanged(SyncPairStatus _) => PushStatus();
    private void OnTransferProgress(FileTransferProgress _) => ThrottledTransferPush();

    private void PushStatus()
    {
        var status = BuildStatus();
        _lastStatus = status;
        InvokeUI(() => UpdateUI(status));
    }

    private SyncStatus BuildStatus()
    {
        SyncEngine[] engines;
        lock (_enginesLock) { engines = _engines.ToArray(); }
        var pairStatuses = engines.Select(e => e.GetStatus()).ToList();
        var status = new SyncStatus
        {
            State                 = _globalPaused ? SyncState.Paused : SyncState.Idle,
            LastSuccessfulSyncUtc = null,
            PairStatuses          = pairStatuses,
        };

        status.State =
            _globalPaused ? SyncState.Paused :
            pairStatuses.Any(p => p.State == SyncState.SafetyHalt) ? SyncState.SafetyHalt :
            pairStatuses.Any(p => p.State == SyncState.DeletionFrozen) ? SyncState.DeletionFrozen :
            pairStatuses.Any(p => p.State == SyncState.Error) ? SyncState.Error :
            pairStatuses.Any(p => p.State == SyncState.Syncing) ? SyncState.Syncing :
            pairStatuses.Count > 0 && pairStatuses.All(p => p.State == SyncState.Paused) ? SyncState.Paused :
            SyncState.Idle;

        status.LastError = pairStatuses
            .Select(p => p.LastError)
            .FirstOrDefault(static e => !string.IsNullOrWhiteSpace(e));

        if (_accountInfo is not null)
        {
            status.AccountEmail      = _accountInfo.Email;
            status.StorageUsedBytes  = _accountInfo.StorageUsedBytes;
            status.StorageTotalBytes = _accountInfo.StorageTotalBytes;
        }

        if (engines.Length > 0)
        {
            status.Metrics = new SyncMetricsSnapshot
            {
                TotalBytesUploaded   = engines.Sum(e => e.Metrics.TotalBytesUploaded),
                TotalBytesDownloaded = engines.Sum(e => e.Metrics.TotalBytesDownloaded),
                TotalApiCalls        = engines.Sum(e => e.Metrics.TotalApiCalls),
                TotalSyncCycles      = engines.Sum(e => e.Metrics.TotalSyncCycles),
                TotalErrors          = engines.Sum(e => e.Metrics.TotalErrors),
                LastCycleDurationMs  = engines.Max(e => e.Metrics.LastCycleDurationMs),
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
        FluentMenuRenderer.Style(_menu);

        _statusItem = new ToolStripMenuItem("SyncIt — Starting...") { Enabled = false };
        _statusItem.Font = new Font(_statusItem.Font, FontStyle.Bold);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Open Dashboard",      null, (_, _) => ShowDashboard());
        _menu.Items.Add("Open Sync Folder",    null, OnOpenSyncFolder);
        _menu.Items.Add("Sync Now",            null, OnSyncNow);
        _pauseItem = new ToolStripMenuItem("Pause Syncing", null, OnPauseResume);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add("Account...",         null, OnSwitchAccount);
        _menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStartHelper.IsEnabled,
        };
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Open Logs Folder",   null, (_, _) => OnOpenLogs());
        _menu.Items.Add("Export Logs (zip)...", null, (_, _) => OnExportLogs());
        _menu.Items.Add($"About SyncIt {AppInfo.Version}", null, (_, _) => OnAbout());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit SyncIt", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Text             = "SyncIt — Starting...",
            Icon             = _iconManager.IdleIcon,
            ContextMenuStrip = _menu,
            Visible          = true,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();
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

        var stateText = status.State switch
        {
            SyncState.Idle    => "Up to date",
            SyncState.Syncing => "Syncing...",
            SyncState.Paused  => "Paused",
            SyncState.Error   => "Error",
            _                 => status.State.ToString(),
        };
        _statusItem.Text = status.AccountEmail is not null
            ? $"SyncIt — {status.AccountEmail} — {stateText}"
            : $"SyncIt — {stateText}";
        _pauseItem.Text = status.State == SyncState.Paused ? "Resume Syncing" : "Pause Syncing";
    }

    // =========================================================================
    // Dashboard
    // =========================================================================

    private void ShowDashboard()
    {
        if (_dashboard is not null && !_dashboard.IsDisposed)
        {
            _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.BringToFront();
            _dashboard.Activate();
            return;
        }

        _dashboard = new DashboardForm(
            getStatus:        () => _lastStatus,
            getTransfers:     () => _activeTransfers,
            syncNow:          () => { foreach (var e in _engines) _ = Task.Run(() => e.TriggerSyncAsync(_engineCts.Token)); },
            pauseResume:      () =>
            {
                _globalPaused = !_globalPaused;
                foreach (var e in _engines) { if (_globalPaused) e.Pause(); else e.Resume(); }
                PushStatus();
            },
            isPaused:         () => _globalPaused,
            fetchActivity:    async () => _activityLog is null ? null : await _activityLog.GetRecentAsync(200, sinceUtc: _sessionStartUtc),
            changeSyncFolder: pairId => ChangeSyncFolder(pairId),
            openSyncFolder:   () => OpenSyncFolder(),
            showAccountForm:  () => ShowAccountForm(),
            addSyncFolder:    () => AddSyncFolder(),
            selectSubfolders:  pairId => SelectSubfolders(pairId),
            fetchAllActivity: async () => _activityLog is null ? null : await _activityLog.GetRecentAsync(500)
        );
        _dashboard.FormClosed += (_, _) => _dashboard = null;
        _dashboard.Show();
    }

    // =========================================================================
    // Menu handlers — direct engine calls (no IPC)
    // =========================================================================

    private void OpenSyncFolder()
    {
        var localPath = _config.SyncPairs.FirstOrDefault(p => p.Enabled)?.LocalPath;
        if (localPath is not null)
        {
            localPath = PathHelper.Expand(localPath);
            if (Directory.Exists(localPath)) { Process.Start("explorer.exe", localPath); return; }
        }
        MessageBox.Show("No sync folder configured.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnOpenSyncFolder(object? sender, EventArgs e) => OpenSyncFolder();

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

    private void ChangeSyncFolder(string pairId)
    {
        var pair = _config.SyncPairs.FirstOrDefault(p => p.Id == pairId);
        if (pair is null)
        {
            _logger.LogWarning("ChangeSyncFolder: pair '{PairId}' not found in config.", pairId);
            return;
        }

        // Run the FolderBrowserDialog on a dedicated STA thread to avoid
        // COM marshalling deadlocks with the WinForms UI thread.
        string? selectedPath = null;
        var thread = new Thread(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description            = $"Choose local folder for \"{pair.RemoteFolderName}\"",
                UseDescriptionForTitle = true,
                SelectedPath           = PathHelper.Expand(pair.LocalPath),
                ShowNewFolderButton    = true,
            };
            if (dialog.ShowDialog() == DialogResult.OK)
                selectedPath = dialog.SelectedPath;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (selectedPath is null) return;

        if (string.Equals(PathHelper.Expand(pair.LocalPath), selectedPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Update config file
        try
        {
            var json   = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<SyncConfig>(json, JsonOpts);
            if (config is null) return;

            var cfgPair = config.SyncPairs.FirstOrDefault(p => p.Id == pairId);
            if (cfgPair is null) return;

            cfgPair.LocalPath = selectedPath;

            var writeOpts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented               = true,
                Converters                  = { new JsonStringEnumConverter() },
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, writeOpts));

            MessageBox.Show(
                $"Sync folder changed to:\n{selectedPath}\n\nSyncIt will restart the sync engine to apply.",
                "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update config:\n{ex.Message}", "SyncIt",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectSubfolders(string pairId)
    {
        if (_driveService is null)
        {
            MessageBox.Show("Not connected to Google Drive yet.", "SyncIt",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var pair = _config.SyncPairs.FirstOrDefault(p => p.Id == pairId);
        if (pair is null) return;

        using var form = new SelectFoldersForm(pair, ConfigPath, _driveService);
        form.ShowDialog();
    }

    private void OnSwitchAccount(object? sender, EventArgs e) => ShowAccountForm();

    private static void OnOpenLogs()
    {
        Directory.CreateDirectory(AppInfo.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppInfo.LogDir}\"") { UseShellExecute = true });
    }

    private void OnExportLogs()
    {
        using var dlg = new SaveFileDialog
        {
            Title    = "Export SyncIt logs",
            Filter   = "Zip archive (*.zip)|*.zip",
            FileName = $"syncit-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var n = LogExporter.Export(AppInfo.LogDir, dlg.FileName);
            MessageBox.Show($"Exported {n} log file(s) to:\n{dlg.FileName}\n\nTokens, secrets and email addresses were redacted. File and folder names are still included; review before sharing.",
                "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log export failed.");
            MessageBox.Show($"Could not export logs: {ex.Message}", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OnAbout() =>
        MessageBox.Show($"SyncIt {AppInfo.Version}\nBidirectional Google Drive sync for Windows.\n\nMIT License - Copyright (c) 2026 SyncIt contributors\nhttps://github.com/madmike30/syncit",
            "About SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAccountForm()
    {
        using var form = new AccountForm(
            _accountInfo?.Email,
            _accountInfo?.StorageUsedBytes ?? 0,
            _accountInfo?.StorageTotalBytes ?? 0,
            SwitchAccountAsync,
            DisconnectAccountAsync);
        form.ShowDialog();
    }

    /// <summary>Shows the sign-in window. Returns true when the user signed in.</summary>
    private bool ShowSignIn()
    {
        using var form = new FirstRunForm();
        return form.ShowDialog() == DialogResult.OK;
    }

    /// <summary>
    /// Disconnect &amp; remove account: stops syncing, revokes Google access, deletes the saved
    /// sign-in, the custom client (if any), the sync state and the sync pairs.
    /// Files in the synced folders and on Google Drive are not touched.
    /// </summary>
    private async Task DisconnectAccountAsync()
    {
        _suppressConfigReload = true;
        try
        {
            _engineCts.Cancel();
            await Task.Run(StopEnginesAsync);
            _configWatcher?.Dispose();
            _configWatcher = null;

            // Revoke at Google so the app no longer has access (best effort; offline is fine).
            if (_oauthService?.Credential is { } credential)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await credential.RevokeTokenAsync(cts.Token);
                    _logger.LogInformation("Google access revoked.");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not revoke Google access (continuing)."); }
            }
            _oauthService = null;

            (_driveService as IDisposable)?.Dispose();
            _driveService = null;
            _overlayStore?.Dispose();
            _overlayStore = null;
            (_activityLog as IDisposable)?.Dispose();
            _activityLog = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await Task.Run(() =>
            {
                var removed = AccountReset.RemoveLocalAccount(ConfigPath, _config);
                if (CredentialHelper.CredentialsExist()) CredentialHelper.DeleteClientCredentials();
                CloudSyncProvider.UnregisterAll("SyncIt", _logger);
                _logger.LogInformation("Account disconnected. Removed {Count} local file(s); sync pairs cleared.", removed.Count);
            });

            _accountInfo = null;
            _lastStatus = null;
            InvokeUI(() =>
            {
                _dashboard?.Close();
                _statusItem.Text = "SyncIt — Not signed in";
                SetTooltip("SyncIt — Not signed in");
            });
        }
        finally { _suppressConfigReload = false; }

        // Start over: shows the sign-in window again.
        _engineCts = new CancellationTokenSource();
        _ = Task.Run(StartSyncEngineAsync);
    }

    /// <summary>
    /// Adds a sync pair chosen by the user. The pair stays off until the user has picked
    /// folders and confirmed, so nothing starts syncing by surprise.
    /// </summary>
    private void AddSyncFolder()
    {
        if (_driveService is null)
        {
            MessageBox.Show("Sign in to Google first.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? selectedPath = null;
        var thread = new Thread(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description            = "Choose the folder on this PC to keep in sync with Google Drive",
                UseDescriptionForTitle = true,
                ShowNewFolderButton    = true,
            };
            if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.SelectedPath;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (selectedPath is null) return;

        try
        {
            var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new SyncConfig();
            var normalized = SyncPairPlanner.NormalizeFolder(selectedPath);
            if (config.SyncPairs.Any(p => SyncPairPlanner.NormalizeFolder(p.LocalPath).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("That folder is already being synced.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var pair = new SyncPair
            {
                Id               = SyncPairPlanner.NextPairId(config.SyncPairs),
                LocalPath        = selectedPath,
                RemoteFolderId   = "root",
                RemoteFolderName = "My Drive",
                Enabled          = false,
            };
            _suppressConfigReload = true;
            try
            {
                config.SyncPairs.Add(pair);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, WriteOpts));

                using (var form = new SelectFoldersForm(pair, ConfigPath, _driveService))
                    form.ShowDialog();

                var start = MessageBox.Show(
                    $"Start syncing now?\n\n{selectedPath}  <->  Google Drive\n\n" +
                    "Files that already exist on both sides are matched by name and content; they are not copied again.",
                    "SyncIt", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? config;
                var saved = config.SyncPairs.FirstOrDefault(p => p.Id == pair.Id);
                if (saved is not null) saved.Enabled = start == DialogResult.Yes;
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, WriteOpts));
            }
            finally { _suppressConfigReload = false; }

            // One explicit restart (the watcher was muted while we edited the file).
            var newConfig = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(ConfigPath), JsonOpts);
            if (newConfig is not null && _driveService is GoogleDriveService drive)
            {
                _config = newConfig;
                _ = Task.Run(async () =>
                {
                    var globFilter = new GlobFilter();
                    var first = SyncPairPlanner.EnabledUnique(_config.SyncPairs).FirstOrDefault();
                    if (first is not null && _overlayStore is null)
                    {
                        _overlayStore = new CloudSyncProvider(PathHelper.Expand(first.LocalPath), "SyncIt", CreateLogger<CloudSyncProvider>());
                        try { await _overlayStore.InitializeAsync(path => globFilter.IsIncluded(path, first), _engineCts.Token); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Explorer overlay unavailable."); }
                    }
                    await StartEnginesAsync(drive, globFilter, new AtomicFileWriter(CreateLogger<AtomicFileWriter>()), _engineCts.Token);
                    PushStatus();
                });
            }
        }
        catch (Exception ex)
        {
            _suppressConfigReload = false;
            MessageBox.Show($"Could not add the folder:\n{ex.Message}", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SwitchAccountAsync()
    {
        _suppressConfigReload = true;
        try { await SwitchAccountCoreAsync(); }
        finally { _suppressConfigReload = false; }
    }

    private async Task SwitchAccountCoreAsync()
    {
        // 1. Stop all sync engines (on background thread to avoid UI deadlock)
        _engineCts.Cancel();
        await Task.Run(StopEnginesAsync);

        // 2. Delete the token file directly to ensure a clean re-auth
        var tokenPath = PathHelper.Expand(_config.TokenStorePath);
        if (File.Exists(tokenPath))
            File.Delete(tokenPath);

        // 3. Re-authorize with account picker (forces Google account selection prompt)
        var oauthService = new OAuthService(_config, CreateLogger<OAuthService>());
        _oauthService = oauthService;
        var credential = await Task.Run(() => oauthService.ReauthorizeAsync());

        // 4. Recreate Drive service with new credential
        (_driveService as IDisposable)?.Dispose();
        var driveService = new GoogleDriveService(credential, CreateLogger<GoogleDriveService>(), _config);
        _driveService = driveService;

        // 5. Fetch new account info immediately
        _accountInfo = await Task.Run(() => driveService.GetAccountInfoAsync());
        _lastAccountFetch = DateTime.UtcNow;

        // 6. Reset stale state databases (old account's file IDs are invalid)
        // Dispose activity log to release its SQLite connection
        (_activityLog as IDisposable)?.Dispose();
        _activityLog = null;

        // Clear SQLite connection pool — without this, pooled connections keep DB files locked
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await Task.Run(() =>
        {
            foreach (var db in new[] { _config.StateDbPath, _config.DeleteJournalPath, _config.ActivityLogPath })
            {
                var path = PathHelper.Expand(db);
                foreach (var ext in new[] { "", "-shm", "-wal" })
                {
                    var f = path + ext;
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Could not delete {File} — SQLite connection may still be held.", f);
                    }
                }
            }
            _logger.LogInformation("Reset state databases for account switch.");
        });

        // 7. Ensure all pairs use Drive root and prompt for folder selection
        var firstPair = _config.SyncPairs.FirstOrDefault(p => p.Enabled);
        if (firstPair is not null)
        {
            // Migrate to Drive root if needed
            if (firstPair.RemoteFolderId != "root")
            {
                firstPair.RemoteFolderId   = "root";
                firstPair.RemoteFolderName = "My Drive";
            }

            // Prompt to select which folders to sync with new account
            var selectResult = MessageBox.Show(
                $"Switched to {_accountInfo.Email}.\n\nWould you like to select which Drive folders to sync?",
                "SyncIt", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (selectResult == DialogResult.Yes)
                SelectSubfolders(firstPair.Id);
        }

        // 8. Recreate activity log (old DB was deleted)
        _engineCts = new CancellationTokenSource();
        var ct = _engineCts.Token;

        _activityLog = new ActivityLog(_config.ActivityLogPath, CreateLogger<ActivityLog>());
        await _activityLog.InitializeAsync(ct);

        // Re-read config in case ChangeDriveFolder updated it
        if (File.Exists(ConfigPath))
        {
            var updatedConfig = JsonSerializer.Deserialize<SyncConfig>(
                await File.ReadAllTextAsync(ConfigPath, ct), JsonOpts);
            if (updatedConfig is not null)
                _config = updatedConfig;
        }

        firstPair = _config.SyncPairs.FirstOrDefault(p => p.Enabled);
        var globFilter   = new GlobFilter();
        if (firstPair is not null)
        {
            var localPath = PathHelper.Expand(firstPair.LocalPath);
            _overlayStore?.Dispose();
            _overlayStore = new CloudSyncProvider(localPath, "SyncIt", CreateLogger<CloudSyncProvider>());
            await _overlayStore.InitializeAsync(
                isSyncedFilter: path => globFilter.IsIncluded(path, firstPair), ct);
        }

        // 9. Restart engines with new credential
        var atomicWriter = new AtomicFileWriter(CreateLogger<AtomicFileWriter>());
        await Task.Run(() => StartEnginesAsync(driveService, globFilter, atomicWriter, ct));

        // 10. Push updated status to UI
        PushStatus();

        // 11. Restart status push loop
        _ = Task.Run(() => StatusPushLoopAsync(ct));

        _logger.LogInformation("Account switched to {Email}", _accountInfo.Email);
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStartHelper.SetEnabled(!AutoStartHelper.IsEnabled);
        _autoStartItem.Checked = AutoStartHelper.IsEnabled;
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        _dashboard?.Close();
        _notifyIcon.Visible = false;
        _engineCts.Cancel();

        // Wait for engines to shut down gracefully (flush DB, release file handles)
        try { await StopEnginesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error during graceful shutdown."); }

        Application.Exit();
    }

    // =========================================================================
    // Deletion freeze (engine event → UI)
    // =========================================================================

    private void OnDeletionFreezeTriggered(string pairId, IReadOnlyList<DeletionRecord> records)
    {
        // Show the warning dialog immediately on the UI thread — don't rely on
        // balloon tips which are easy to miss.
        InvokeUI(() =>
        {
            _notifyIcon.Icon = _iconManager.WarningIcon;

            using var form = new DeletionWarningForm(pairId, _lastStatus);
            var result = form.ShowDialog();

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
        });
    }

    // =========================================================================
    // Transfer progress (throttled to 1Hz)
    // =========================================================================

    private void ThrottledTransferPush()
    {
        if ((DateTime.UtcNow - _lastTransferPush).TotalSeconds < 1) return;
        _lastTransferPush = DateTime.UtcNow;
        SyncEngine[] snapshot;
        lock (_enginesLock) { snapshot = _engines.ToArray(); }
        _activeTransfers = snapshot.SelectMany(e => e.TransferTracker.ActiveTransfers).ToList();
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
