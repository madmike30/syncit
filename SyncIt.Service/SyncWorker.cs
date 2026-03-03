using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Ipc;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;

namespace SyncIt.Service;

/// <summary>
/// IHostedService that starts and manages all sync engines and the IPC server.
/// One SyncEngine per enabled SyncPair.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SyncWorker : BackgroundService
{
    private readonly ILogger<SyncWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHostApplicationLifetime _appLifetime;

    private SyncConfig _config;
    private IpcServer? _ipcServer;
    private readonly List<SyncEngine> _engines = new();
    private readonly List<DeletionCoordinator> _deletionCoordinators = new();
    private readonly IConfigWatcher _configWatcher;
    private CancellationToken _stoppingToken;

    private bool _globalPaused;
    private IGoogleDriveService? _driveService;
    private IOverlayStateStore? _overlayStore;
    private DateTime _lastTransferPush = DateTime.MinValue;

    // Cached account info — refreshed every 5 minutes
    private DriveAccountInfo? _accountInfo;
    private DateTime _lastAccountFetch = DateTime.MinValue;

    public SyncWorker(
        ILogger<SyncWorker> logger,
        ILoggerFactory loggerFactory,
        IConfigWatcher configWatcher,
        IHostApplicationLifetime appLifetime,
        SyncConfig config)
    {
        _logger        = logger;
        _loggerFactory = loggerFactory;
        _configWatcher = configWatcher;
        _appLifetime   = appLifetime;
        _config        = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("SyncIt Service starting...");

        try
        {
            // Initialize databases
            await InitializeDatabasesAsync(stoppingToken);

            // Initialize OAuth (loads DPAPI token; may trigger browser on first run)
            var oauthService = CreateOAuthService();
            var credential   = await oauthService.InitializeAsync(stoppingToken);

            // Create shared services
            var driveService = new GoogleDriveService(credential, CreateLogger<GoogleDriveService>());
            _driveService = driveService;
            var globFilter   = new GlobFilter();
            var atomicWriter = new AtomicFileWriter(CreateLogger<AtomicFileWriter>());

            // Initialize cloud sync provider for Explorer Status column icons
            var firstPair = _config.SyncPairs.FirstOrDefault(p => p.Enabled);
            if (firstPair is not null)
            {
                var localPath = PathHelper.Expand(firstPair.LocalPath);
                _overlayStore = new CloudSyncProvider(localPath, "SyncIt", CreateLogger<CloudSyncProvider>());
                await _overlayStore.InitializeAsync(stoppingToken);
            }

            // Start IPC server
            _ipcServer = new IpcServer(_config, CreateLogger<IpcServer>());
            _ipcServer.CommandReceived += OnCommandReceivedAsync;
            _ipcServer.Start();

            // Start engines for each enabled pair
            await StartEnginesAsync(driveService, globFilter, atomicWriter, stoppingToken);

            // React to config hot-reload (BUG-016 fix: observe errors from async handler)
            _configWatcher.ConfigReloaded += newConfig =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Config reloaded — restarting engines...");
                        await StopEnginesAsync();
                        _config = newConfig;
                        await StartEnginesAsync(driveService, globFilter, atomicWriter, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to restart engines after config reload.");
                    }
                });
            };
            _configWatcher.Start();

            // Push status updates every 10 seconds
            _ = Task.Run(() => StatusPushLoopAsync(stoppingToken), stoppingToken);

            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "SyncIt Service fatal error.");
            throw;
        }
        finally
        {
            await StopEnginesAsync();
            _overlayStore?.Dispose();
            if (_ipcServer is not null) await _ipcServer.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Engine management
    // -------------------------------------------------------------------------

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

            var trashManager   = new LocalTrashManager(_config.LocalTrashPath, CreateLogger<LocalTrashManager>());
            var safetyValidator = new DeletionSafetyValidator(_config, stateStore, deletionJournal, CreateLogger<DeletionSafetyValidator>());
            var deletionCoord  = new DeletionCoordinator(
                _config, safetyValidator, deletionJournal, trashManager,
                driveService, stateStore, atomicWriter, CreateLogger<DeletionCoordinator>());

            deletionCoord.DeletionFreezeTriggered += OnDeletionFreezeTriggered;
            _deletionCoordinators.Add(deletionCoord);

            var changeQueue   = new ChangeQueue();
            var conflictRes   = new ConflictResolver(CreateLogger<ConflictResolver>());

            var engine = new SyncEngine(
                pair, _config, driveService, stateStore, conflictRes,
                deletionCoord, globFilter, changeQueue, safetyValidator,
                atomicWriter, CreateLogger<SyncEngine>(), _overlayStore);

            engine.StatusChanged += OnEngineStatusChanged;
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

    // -------------------------------------------------------------------------
    // IPC command handling
    // -------------------------------------------------------------------------

    private async Task OnCommandReceivedAsync(IpcMessage msg)
    {
        _logger.LogDebug("IPC command: {Type}", msg.Type);

        switch (msg.Type)
        {
            case IpcMessageType.Pause:
                _globalPaused = true;
                foreach (var e in _engines) e.Pause();
                await SendAck(IpcMessageType.Pause);
                break;

            case IpcMessageType.Resume:
                _globalPaused = false;
                foreach (var e in _engines) e.Resume();
                await SendAck(IpcMessageType.Resume);
                break;

            case IpcMessageType.RequestStatus:
                await PushStatusAsync();
                break;

            // BUG-004 fix: pass _stoppingToken instead of CancellationToken.None
            case IpcMessageType.TriggerSync:
                var triggerPayload = IpcSerializer.DeserializePayload<TriggerSyncPayload>(msg.Payload);
                foreach (var e in _engines)
                    _ = Task.Run(() => e.TriggerSyncAsync(_stoppingToken));
                await SendAck(IpcMessageType.TriggerSync);
                break;

            // BUG-008 fix: actually wire up ApproveDeletions/CancelDeletions to coordinator
            case IpcMessageType.ApproveDeletions:
                var approvePairId = msg.Payload;
                _logger.LogInformation("User approved frozen deletions for pair {PairId}", approvePairId);
                foreach (var coord in _deletionCoordinators)
                {
                    try { await coord.ApproveFrozenAsync(approvePairId!, _stoppingToken); }
                    catch (Exception ex) { _logger.LogError(ex, "Error approving frozen deletions for {PairId}", approvePairId); }
                }
                await SendAck(IpcMessageType.ApproveDeletions);
                break;

            case IpcMessageType.CancelDeletions:
                var cancelPairId = msg.Payload;
                _logger.LogInformation("User cancelled frozen deletions for pair {PairId}", cancelPairId);
                foreach (var coord in _deletionCoordinators)
                {
                    try { await coord.CancelFrozenAsync(cancelPairId!, _stoppingToken); }
                    catch (Exception ex) { _logger.LogError(ex, "Error cancelling frozen deletions for {PairId}", cancelPairId); }
                }
                await SendAck(IpcMessageType.CancelDeletions);
                break;

            case IpcMessageType.GetSubfolders:
                var subPayload = IpcSerializer.DeserializePayload<GetSubfoldersPayload>(msg.Payload);
                if (subPayload is not null)
                    _ = Task.Run(() => HandleGetSubfoldersAsync(subPayload, _stoppingToken));
                break;

            // BUG-009 fix: use graceful shutdown instead of Environment.Exit(0)
            case IpcMessageType.Shutdown:
                _logger.LogInformation("Tray requested service shutdown.");
                await SendAck(IpcMessageType.Shutdown);
                _appLifetime.StopApplication();
                break;
        }
    }

    private async Task HandleGetSubfoldersAsync(GetSubfoldersPayload request, CancellationToken ct)
    {
        SubfoldersResultPayload result;
        try
        {
            var pair = _config.SyncPairs.FirstOrDefault(p => p.Id == request.SyncPairId);
            if (pair is null || _driveService is null)
            {
                result = new SubfoldersResultPayload
                {
                    SyncPairId = request.SyncPairId,
                    Error = pair is null ? "Sync pair not found." : "Drive service not ready.",
                };
            }
            else
            {
                var subfolders = await _driveService.ListSubfoldersAsync(pair.RemoteFolderId, ct: ct);
                result = new SubfoldersResultPayload
                {
                    SyncPairId = request.SyncPairId,
                    Subfolders = subfolders.Select(s => new SubfolderEntry
                    {
                        Name = s.Name,
                        RelativePath = s.RelativePath,
                        DriveId = s.DriveId,
                    }).ToList(),
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list subfolders for pair {PairId}", request.SyncPairId);
            result = new SubfoldersResultPayload
            {
                SyncPairId = request.SyncPairId,
                Error = $"Could not list Drive folders: {ex.Message}",
            };
        }

        if (_ipcServer is not null)
            await _ipcServer.SendAsync(IpcSerializer.Build(IpcMessageType.SubfoldersResult, result));
    }

    private void OnEngineStatusChanged(SyncPairStatus status) =>
        _ = Task.Run(PushStatusAsync);

    // BUG-019 fix: observe errors from IPC send so tray gets notified about freeze
    private void OnDeletionFreezeTriggered(string pairId, IReadOnlyList<DeletionRecord> records)
    {
        var payload = new DeletionFreezePayload
        {
            SyncPairId = pairId,
            Count      = records.Count,
            Reason     = $"{records.Count} files would be deleted",
            FilePaths  = records.Select(r => r.RelativePath).ToList(),
            FileSizes  = records.Select(r => r.SizeBytes).ToList(),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                if (_ipcServer is not null)
                    await _ipcServer.SendAsync(
                        IpcSerializer.Build(IpcMessageType.DeletionFreezeTriggered, payload));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send deletion freeze notification to tray for pair {PairId}", pairId);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Status push
    // -------------------------------------------------------------------------

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

            await PushStatusAsync();
        }
    }

    private async Task PushStatusAsync()
    {
        if (_ipcServer is null) return;
        var status = BuildStatus();
        await _ipcServer.SendAsync(IpcSerializer.Build(IpcMessageType.StatusUpdate, status));
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
            status.AccountEmail     = _accountInfo.Email;
            status.StorageUsedBytes = _accountInfo.StorageUsedBytes;
            status.StorageTotalBytes = _accountInfo.StorageTotalBytes;
        }

        return status;
    }

    private async Task SendAck(IpcMessageType inResponseTo) =>
        await _ipcServer!.SendAsync(IpcSerializer.Build(IpcMessageType.Ack, new AckPayload { InResponseTo = inResponseTo }));

    // -------------------------------------------------------------------------
    // Transfer progress push (throttled to 1Hz)
    // -------------------------------------------------------------------------

    private void ThrottledTransferPush()
    {
        if ((DateTime.UtcNow - _lastTransferPush).TotalSeconds < 1) return;
        _lastTransferPush = DateTime.UtcNow;
        _ = Task.Run(PushTransferStatusAsync);
    }

    private async Task PushTransferStatusAsync()
    {
        if (_ipcServer is null) return;
        try
        {
            var allActive = _engines.SelectMany(e => e.TransferTracker.ActiveTransfers).ToList();
            var payload = new FileTransferUpdatePayload { ActiveTransfers = allActive };
            await _ipcServer.SendAsync(IpcSerializer.Build(IpcMessageType.FileTransferUpdate, payload));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to push transfer status.");
        }
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private IOAuthService CreateOAuthService() =>
        new OAuthService(_config, CreateLogger<OAuthService>());

    private IFileStateStore CreateStateStore() =>
        new FileStateStore(_config.StateDbPath, CreateLogger<FileStateStore>());

    private DeletionJournal CreateDeletionJournal() =>
        new DeletionJournal(_config.DeleteJournalPath, CreateLogger<DeletionJournal>());

    private async Task InitializeDatabasesAsync(CancellationToken ct)
    {
        var stateStore = CreateStateStore();
        await stateStore.InitializeAsync(ct);

        var journal = CreateDeletionJournal();
        await journal.InitializeAsync(ct);
    }

    private ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();
}
