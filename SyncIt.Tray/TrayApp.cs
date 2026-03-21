using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Ipc;
using SyncIt.Core.Models;
using SyncIt.Tray.Forms;

namespace SyncIt.Tray;

/// <summary>
/// ApplicationContext that owns the NotifyIcon lifecycle, the IPC client connection,
/// and the background service process. This is the single entry point for SyncIt —
/// launching the tray app automatically starts the sync service as a hidden child process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApp : ApplicationContext
{
    internal static readonly string ConfigPath =
        PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");

    private static readonly string LogDirectory =
        PathHelper.Expand(@"%APPDATA%\SyncIt\logs");

    private readonly IpcClient _ipcClient;
    private readonly TrayIconManager _iconManager;
    private readonly ILogger<TrayApp> _logger;
    private readonly TraySettings _traySettings;
    private SyncConfig? _config;

    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _menu = null!;

    // Named menu items we update dynamically
    private ToolStripMenuItem _accountItem = null!;
    private ToolStripMenuItem _storageItem = null!;
    private ToolStripMenuItem _activityItem1 = null!;
    private ToolStripMenuItem _activityItem2 = null!;
    private ToolStripMenuItem _activityItem3 = null!;
    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _autoStartItem = null!;
    private ToolStripMenuItem _notificationsItem = null!;

    private SyncStatus? _lastStatus;
    private List<FileTransferProgress>? _activeTransfers;
    private string? _pendingFreezeForPair;

    // Track recent completed transfers for inline display
    private readonly List<string> _recentActivity = new();
    private const int MaxRecentItems = 3;

    // Background service process — launched and owned by the tray app
    private Process? _serviceProcess;

    // Pending subfolder request — set when the user opens Select Folders
    private TaskCompletionSource<SubfoldersResultPayload?>? _pendingSubfolders;

    // Pending activity history request
    private TaskCompletionSource<ActivityHistoryPayload?>? _pendingActivityHistory;

    public TrayApp(IpcClient ipcClient, ILogger<TrayApp> logger, SyncConfig? config = null)
    {
        _ipcClient    = ipcClient;
        _iconManager  = new TrayIconManager();
        _logger       = logger;
        _config       = config;
        _traySettings = TraySettings.Load();

        InitializeNotifyIcon();
        LaunchServiceProcess();
        StartIpc();
    }

    // -------------------------------------------------------------------------
    // Service process management
    // -------------------------------------------------------------------------

    private void LaunchServiceProcess()
    {
        try
        {
            // Find the service executable/project
            var exeDir = AppContext.BaseDirectory;
            var serviceExe = Path.Combine(exeDir, "SyncIt.Service.exe");

            ProcessStartInfo psi;
            if (File.Exists(serviceExe))
            {
                // Published: service exe next to tray exe
                psi = new ProcessStartInfo
                {
                    FileName        = serviceExe,
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                };
            }
            else
            {
                // Development: use dotnet run
                var solutionDir = FindSolutionDir(exeDir);
                if (solutionDir is null)
                {
                    _logger.LogWarning("Cannot find SyncIt.Service project. Service must be started manually.");
                    return;
                }

                psi = new ProcessStartInfo
                {
                    FileName        = "dotnet",
                    Arguments       = $"run --project \"{Path.Combine(solutionDir, "SyncIt.Service")}\" --no-build",
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                };
            }

            _serviceProcess = Process.Start(psi);
            if (_serviceProcess is not null)
            {
                _serviceProcess.EnableRaisingEvents = true;
                _serviceProcess.Exited += OnServiceExited;
                _logger.LogInformation("Service process started (PID {Pid})", _serviceProcess.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start service process.");
        }
    }

    private void OnServiceExited(object? sender, EventArgs e)
    {
        if (_serviceProcess is null) return;
        _logger.LogWarning("Service process exited with code {Code}. Restarting in 5s...",
            _serviceProcess.ExitCode);

        // Auto-restart after a delay (unless we're shutting down)
        Task.Delay(5000).ContinueWith(_ =>
        {
            if (!_notifyIcon.Visible) return; // already exiting
            _notifyIcon.ContextMenuStrip?.Invoke(() =>
            {
                _logger.LogInformation("Restarting service process...");
                LaunchServiceProcess();
            });
        });
    }

    private void StopServiceProcess()
    {
        if (_serviceProcess is null || _serviceProcess.HasExited) return;

        _serviceProcess.EnableRaisingEvents = false; // prevent restart loop
        _serviceProcess.Exited -= OnServiceExited;

        try
        {
            // Try graceful shutdown via IPC first
            _ = _ipcClient.SendAsync(IpcSerializer.Build(IpcMessageType.Shutdown));
            if (!_serviceProcess.WaitForExit(5000))
            {
                _logger.LogWarning("Service did not exit gracefully, killing...");
                _serviceProcess.Kill(entireProcessTree: true);
                _serviceProcess.WaitForExit(3000);
            }
            _logger.LogInformation("Service process stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping service process.");
            try { _serviceProcess.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _serviceProcess.Dispose();
            _serviceProcess = null;
        }
    }

    private static string? FindSolutionDir(string startDir)
    {
        // Walk up from bin/Debug/net8.0-windows to find SyncIt.sln
        var dir = startDir;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "SyncIt.sln")))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // NotifyIcon setup — full menu layout
    // -------------------------------------------------------------------------

    private void InitializeNotifyIcon()
    {
        _menu = new ContextMenuStrip();
        _menu.ShowImageMargin = false;

        // --- Account / Storage header ---
        _accountItem = new ToolStripMenuItem("SyncIt") { Enabled = false };
        _accountItem.Font = new Font(_accountItem.Font, FontStyle.Bold);
        _menu.Items.Add(_accountItem);

        _storageItem = new ToolStripMenuItem("Connecting...") { Enabled = false };
        _menu.Items.Add(_storageItem);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Inline activity (last 3 items) ---
        _activityItem1 = new ToolStripMenuItem("No recent activity") { Enabled = false };
        _activityItem2 = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _activityItem3 = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _menu.Items.Add(_activityItem1);
        _menu.Items.Add(_activityItem2);
        _menu.Items.Add(_activityItem3);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Primary actions ---
        _menu.Items.Add("Open SyncIT Folder", null, OnOpenSyncFolder);
        _menu.Items.Add("Open Status...",      null, OnOpenStatus);
        _menu.Items.Add("Activity History...", null, OnOpenActivityHistory);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Sync Now",            null, OnSyncNow);
        _pauseItem = new ToolStripMenuItem("Pause Syncing", null, OnPauseResume);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add("Select Folders...",   null, OnSelectFolders);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Settings ---
        _menu.Items.Add("Preferences...",      null, OnOpenPreferences);
        _menu.Items.Add("Open Log Folder",     null, OnOpenLogFolder);

        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStartHelper.IsEnabled,
            CheckOnClick = false,
        };
        _menu.Items.Add(_autoStartItem);

        _notificationsItem = new ToolStripMenuItem("Notifications", null, OnToggleNotifications)
        {
            Checked = _traySettings.NotificationsEnabled,
            CheckOnClick = false,
        };
        _menu.Items.Add(_notificationsItem);

        _menu.Items.Add(new ToolStripSeparator());

        // --- Exit ---
        _menu.Items.Add("Quit SyncIt",         null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Text             = "SyncIt — Starting...",
            Icon             = _iconManager.IdleIcon,
            ContextMenuStrip = _menu,
            Visible          = true,
        };

        _notifyIcon.DoubleClick += OnOpenStatus;
    }

    // -------------------------------------------------------------------------
    // IPC
    // -------------------------------------------------------------------------

    private void StartIpc()
    {
        _ipcClient.MessageReceived += OnIpcMessage;
        _ipcClient.Start();
    }

    private void OnIpcMessage(IpcMessage msg)
    {
        // Dispatch to UI thread
        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _notifyIcon.ContextMenuStrip.Invoke(() => OnIpcMessage(msg));
            return;
        }

        switch (msg.Type)
        {
            case IpcMessageType.StatusUpdate:
                var status = IpcSerializer.DeserializePayload<SyncStatus>(msg.Payload);
                if (status is not null) UpdateStatus(status);
                break;

            case IpcMessageType.ErrorOccurred:
                var err = IpcSerializer.DeserializePayload<ErrorPayload>(msg.Payload);
                if (err is not null) ShowError(err);
                break;

            case IpcMessageType.AuthRequired:
                ShowBalloon("SyncIt", "Google authentication required. Click to re-authorize.", ToolTipIcon.Warning);
                break;

            case IpcMessageType.DeletionFreezeTriggered:
                var freeze = IpcSerializer.DeserializePayload<DeletionFreezePayload>(msg.Payload);
                if (freeze is not null) ShowDeletionFreeze(freeze);
                break;

            case IpcMessageType.FileTransferUpdate:
                var transferPayload = IpcSerializer.DeserializePayload<FileTransferUpdatePayload>(msg.Payload);
                if (transferPayload is not null)
                {
                    _activeTransfers = transferPayload.ActiveTransfers;
                    UpdateActivityItems();
                }
                break;

            case IpcMessageType.SubfoldersResult:
                var subResult = IpcSerializer.DeserializePayload<SubfoldersResultPayload>(msg.Payload);
                _pendingSubfolders?.TrySetResult(subResult);
                break;

            case IpcMessageType.ActivityHistoryResult:
                var histResult = IpcSerializer.DeserializePayload<ActivityHistoryPayload>(msg.Payload);
                _pendingActivityHistory?.TrySetResult(histResult);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Status updates
    // -------------------------------------------------------------------------

    private void UpdateStatus(SyncStatus status)
    {
        _lastStatus = status;

        var icon = status.State switch
        {
            SyncState.Syncing        => _iconManager.SyncingIcon,
            SyncState.Paused         => _iconManager.PausedIcon,
            SyncState.Error          => _iconManager.ErrorIcon,
            SyncState.SafetyHalt     => _iconManager.ErrorIcon,
            SyncState.DeletionFrozen => _iconManager.WarningIcon,
            _                        => _iconManager.IdleIcon,
        };

        _notifyIcon.Icon = icon;
        _notifyIcon.Text = BuildTooltip(status);

        // Update account header
        if (status.AccountEmail is not null)
            _accountItem.Text = $"SyncIt — {status.AccountEmail}";

        // Update storage display
        UpdateStorageDisplay(status);

        // Update pause/resume menu item
        _pauseItem.Text = status.State == SyncState.Paused ? "Resume Syncing" : "Pause Syncing";

        // Track completed uploads/downloads for recent activity
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
        else if (status.StorageUsedBytes > 0)
        {
            _storageItem.Text = $"Storage: {FormatBytes(status.StorageUsedBytes)} used";
        }
        else if (status.AccountEmail is not null)
        {
            var stateText = status.State switch
            {
                SyncState.Idle    => "Up to date",
                SyncState.Syncing => "Syncing...",
                SyncState.Paused  => "Paused",
                SyncState.Error   => "Error",
                _ => status.State.ToString(),
            };
            _storageItem.Text = stateText;
        }
    }

    private void UpdateActivityItems()
    {
        var items = new List<string>();

        // Show active transfers first
        if (_activeTransfers is not null)
        {
            foreach (var t in _activeTransfers.Take(MaxRecentItems))
            {
                var dir  = t.Direction == TransferDirection.Upload ? "\u2191" : "\u2193";
                var name = Path.GetFileName(t.RelativePath);
                items.Add($"{dir} {name} — {t.ProgressPercent:F0}%");
            }
        }

        // Fill remaining slots with recent completed items
        foreach (var recent in _recentActivity)
        {
            if (items.Count >= MaxRecentItems) break;
            items.Add(recent);
        }

        // Update the menu items
        SetActivityItem(_activityItem1, items.Count > 0 ? items[0] : "No recent activity", items.Count > 0);
        SetActivityItem(_activityItem2, items.Count > 1 ? items[1] : "", items.Count > 1);
        SetActivityItem(_activityItem3, items.Count > 2 ? items[2] : "", items.Count > 2);
    }

    private static void SetActivityItem(ToolStripMenuItem item, string text, bool visible)
    {
        item.Text = text;
        item.Visible = visible || text == "No recent activity";
    }

    private void UpdateRecentActivity(SyncStatus status)
    {
        // Use session counters to detect new completions
        var totalCompleted = status.FilesUploadedSession + status.FilesDownloadedSession;
        if (totalCompleted > 0 && status.CurrentActivity is not null &&
            status.State == SyncState.Idle)
        {
            // If we just went idle and have activity info, it means a sync completed
        }

        // When pair statuses contain info about last synced file, add to recent
        foreach (var pair in status.PairStatuses)
        {
            if (pair.State == SyncState.Idle && pair.PendingChanges == 0)
            {
                // Pair is up to date — show in recent as synced
                var displayName = pair.RemoteFolderName;
                var entry = $"\u2713 {displayName} — up to date";
                if (_recentActivity.Count == 0 || _recentActivity[0] != entry)
                {
                    _recentActivity.Insert(0, entry);
                    if (_recentActivity.Count > MaxRecentItems)
                        _recentActivity.RemoveAt(_recentActivity.Count - 1);
                }
            }
        }

        UpdateActivityItems();
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

        var tip = $"SyncIt — {state}";
        if (status.AccountEmail is not null)
            tip = $"SyncIt — {status.AccountEmail}\n{state}";
        if (status.LastSuccessfulSyncUtc.HasValue)
            tip += $"\nLast sync: {status.LastSuccessfulSyncUtc.Value.ToLocalTime():HH:mm}";
        return tip[..Math.Min(tip.Length, 63)]; // NotifyIcon tooltip max 63 chars
    }

    private void ShowError(ErrorPayload err)
    {
        _notifyIcon.Icon = _iconManager.ErrorIcon;
        ShowBalloon("SyncIt Error",
            $"{err.Message}\n(Pair: {err.SyncPairId})",
            err.Severity == ErrorSeverity.Fatal ? ToolTipIcon.Error : ToolTipIcon.Warning);
    }

    private void ShowDeletionFreeze(DeletionFreezePayload freeze)
    {
        _pendingFreezeForPair = freeze.SyncPairId;
        _notifyIcon.Icon = _iconManager.WarningIcon;
        ShowBalloon("SyncIt — Deletion Safety",
            $"{freeze.Count} files would be deleted. Click to review.",
            ToolTipIcon.Warning);

        // Show the dialog on balloon click
        _notifyIcon.BalloonTipClicked += OnFreezeDialogRequested;
    }

    private void OnFreezeDialogRequested(object? sender, EventArgs e)
    {
        _notifyIcon.BalloonTipClicked -= OnFreezeDialogRequested;
        if (_pendingFreezeForPair is null) return;

        using var form = new DeletionWarningForm(_pendingFreezeForPair, _lastStatus);
        var result = form.ShowDialog();

        var pairId = _pendingFreezeForPair;
        _pendingFreezeForPair = null;

        if (result == DialogResult.Yes)
            _ = _ipcClient.SendAsync(IpcSerializer.Build(IpcMessageType.ApproveDeletions, pairId));
        else
            _ = _ipcClient.SendAsync(IpcSerializer.Build(IpcMessageType.CancelDeletions, pairId));
    }

    // -------------------------------------------------------------------------
    // Menu actions
    // -------------------------------------------------------------------------

    private void OnOpenSyncFolder(object? sender, EventArgs e)
    {
        var localPath = _config?.SyncPairs.FirstOrDefault(p => p.Enabled)?.LocalPath;
        if (localPath is not null)
        {
            localPath = PathHelper.Expand(localPath);
            if (Directory.Exists(localPath))
            {
                Process.Start("explorer.exe", localPath);
                return;
            }
        }
        MessageBox.Show("No sync folder is configured or folder does not exist.",
            "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnOpenStatus(object? sender, EventArgs e)
    {
        using var form = new StatusForm(_lastStatus, _activeTransfers);
        form.ShowDialog();
    }

    private void OnSyncNow(object? sender, EventArgs e) =>
        _ = _ipcClient.SendAsync(IpcSerializer.Build(IpcMessageType.TriggerSync));

    private void OnPauseResume(object? sender, EventArgs e)
    {
        var isPaused = _lastStatus?.State == SyncState.Paused;
        _ = _ipcClient.SendAsync(IpcSerializer.Build(isPaused ? IpcMessageType.Resume : IpcMessageType.Pause));
    }

    private void OnSelectFolders(object? sender, EventArgs e)
    {
        // Load config from disk so we always have the current pair list
        SyncConfig? config = _config;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = System.Text.Json.JsonSerializer.Deserialize<SyncConfig>(json,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling         = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas         = true,
                        Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-read config for folder selection.");
            }
        }

        var pairs = config?.SyncPairs ?? new();
        if (pairs.Count == 0)
        {
            MessageBox.Show("No sync pairs are configured.", "SyncIt",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // If there are multiple pairs, ask the user which one to configure
        SyncPair? target;
        if (pairs.Count == 1)
        {
            target = pairs[0];
        }
        else
        {
            using var picker = new PairPickerDialog(pairs);
            if (picker.ShowDialog() != DialogResult.OK || picker.SelectedPair is null) return;
            target = picker.SelectedPair;
        }

        // Send the GetSubfolders request to the service and wait for the response in the form
        _pendingSubfolders = new TaskCompletionSource<SubfoldersResultPayload?>();
        _ = _ipcClient.SendAsync(
            IpcSerializer.Build(IpcMessageType.GetSubfolders,
                new GetSubfoldersPayload { SyncPairId = target.Id }));

        using var form = new SelectFoldersForm(target, ConfigPath, _pendingSubfolders.Task);
        form.ShowDialog();

        _pendingSubfolders = null;

        // The form wrote to syncit.json; the service will hot-reload within ~2 s
    }

    private void OnOpenActivityHistory(object? sender, EventArgs e)
    {
        using var form = new ActivityHistoryForm(async () =>
        {
            _pendingActivityHistory = new TaskCompletionSource<ActivityHistoryPayload?>();
            await _ipcClient.SendAsync(
                IpcSerializer.Build(IpcMessageType.RequestActivityHistory,
                    new ActivityHistoryRequest { Limit = 200 }));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => _pendingActivityHistory?.TrySetResult(null));

            var result = await _pendingActivityHistory.Task;
            _pendingActivityHistory = null;
            return result?.Records;
        });
        form.ShowDialog();
    }

    private void OnOpenPreferences(object? sender, EventArgs e)
    {
        if (File.Exists(ConfigPath))
        {
            // Open in the user's default editor
            Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show($"Config file not found:\n{ConfigPath}",
                "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        if (Directory.Exists(LogDirectory))
            Process.Start("explorer.exe", LogDirectory);
        else
            MessageBox.Show($"Log folder not found:\n{LogDirectory}",
                "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        var newState = !AutoStartHelper.IsEnabled;
        AutoStartHelper.SetEnabled(newState);
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
        StopServiceProcess();
        _ = _ipcClient.DisposeAsync().AsTask();
        Application.Exit();
    }

    // -------------------------------------------------------------------------
    // First-run OAuth
    // -------------------------------------------------------------------------

    public void RunAuthOnly()
    {
        using var form = new FirstRunForm();
        form.ShowDialog();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (!_traySettings.NotificationsEnabled) return;
        _notifyIcon.ShowBalloonTip(5000, title, text, icon);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _iconManager.Dispose();
            StopServiceProcess();
            _ = _ipcClient.DisposeAsync().AsTask();
        }
        base.Dispose(disposing);
    }
}
