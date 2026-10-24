using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace SyncIt.Core.Services;

/// <summary>
/// Registers the sync folder as a Windows Cloud Files sync root and sets
/// per-file sync status via the Cloud Filter API (cldapi.dll).
/// This drives the Status column in File Explorer.
///
/// Only actively synced items (matching SelectedSubfolders filter) get the
/// green checkmark (in-sync + pinned). Non-synced items are marked in-sync
/// but unpinned — they show no icon in the Status column.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class CloudSyncProvider : IOverlayStateStore
{
    private readonly string _syncRootPath;
    private readonly string _syncRootId;
    private readonly string _displayName;
    private readonly ILogger _logger;
    private bool _registered;
    private long _connectionKey;

    // Tracks how many non-synced files exist under each directory.
    private readonly ConcurrentDictionary<string, int> _dirNotSyncedCount = new(StringComparer.OrdinalIgnoreCase);

    // Tracks the last-known synced state per item to prevent PropagateParentState from
    // being called multiple times for the same file crossing the same boundary.
    // Without this, Pending → Syncing → Synced increments the parent counter twice
    // but only decrements it once, leaving it permanently > 0 (folder stays EXCLUDED).
    private readonly ConcurrentDictionary<string, bool> _itemWasSynced = new(StringComparer.OrdinalIgnoreCase);

    public CloudSyncProvider(string syncRootPath, string providerId, ILogger logger, string? displayName = null)
    {
        _syncRootPath = Path.GetFullPath(syncRootPath);
        _logger = logger;
        _displayName = displayName ?? Path.GetFileName(_syncRootPath);

        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        _syncRootId = $"{providerId}!{sid}!default";
    }

    public async Task InitializeAsync(Func<string, bool>? isSyncedFilter = null, CancellationToken ct = default)
    {
        try
        {
            await RegisterSyncRootAsync();
            ConnectSyncRoot();
            _registered = true;
            _logger.LogInformation("Cloud sync root registered and connected: {Path}", _syncRootPath);

            MarkItems(isSyncedFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register cloud sync root at {Path}", _syncRootPath);
        }
    }

    /// <summary>
    /// Marks all items under the sync root as placeholders.
    /// Synced items (passing filter) get green checkmarks (in-sync + pinned).
    /// Non-synced items get in-sync only (no icon) to suppress the sync-arrows.
    /// </summary>
    private void MarkItems(Func<string, bool>? isSyncedFilter)
    {
        int synced = 0, suppressed = 0, failed = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_syncRootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_syncRootPath, dir).Replace('\\', '/');
                    bool isSynced = isSyncedFilter is null || isSyncedFilter(relativePath);
                    EnsurePlaceholder(dir);
                    SetInSyncState(dir, true, pin: isSynced);
                    _itemWasSynced[dir] = isSynced;
                    if (isSynced) synced++; else suppressed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "Failed to update directory {Path}", dir);
                }
            }

            foreach (var file in Directory.EnumerateFiles(_syncRootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_syncRootPath, file).Replace('\\', '/');
                    bool isSynced = isSyncedFilter is null || isSyncedFilter(relativePath);
                    EnsurePlaceholder(file);
                    SetInSyncState(file, true, pin: isSynced);
                    _itemWasSynced[file] = isSynced;
                    if (isSynced) synced++; else suppressed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "Failed to update {Path}", file);
                }
            }
            _logger.LogInformation("Overlay: {Synced} synced (green), {Suppressed} suppressed (no icon), {Failed} failed",
                synced, suppressed, failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning sync root for placeholder conversion");
        }
    }

    public Task SetStateAsync(string absolutePath, OverlaySyncState state, CancellationToken ct = default)
    {
        if (!_registered) return Task.CompletedTask;
        try
        {
            bool inSync = state == OverlaySyncState.Synced;
            EnsurePlaceholder(absolutePath);
            SetInSyncState(absolutePath, inSync, pin: true);
            PropagateParentStateIfChanged(absolutePath, inSync);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set cloud state for '{Path}'", absolutePath);
        }
        return Task.CompletedTask;
    }

    public Task SetBatchAsync(IEnumerable<(string path, OverlaySyncState state)> items, CancellationToken ct = default)
    {
        if (!_registered) return Task.CompletedTask;
        foreach (var (path, state) in items)
        {
            try
            {
                bool inSync = state == OverlaySyncState.Synced;
                EnsurePlaceholder(path);
                SetInSyncState(path, inSync, pin: true);
                PropagateParentStateIfChanged(path, inSync);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set cloud state for '{Path}'", path);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Only propagates parent state when the item crosses the synced/not-synced boundary.
    /// Guards against double-counting when the same file transitions Pending → Syncing → Synced:
    /// without this, the parent counter would be incremented twice and decremented once,
    /// leaving it permanently > 0 and the folder permanently EXCLUDED (no icon).
    /// </summary>
    private void PropagateParentStateIfChanged(string absolutePath, bool inSync)
    {
        bool wasSynced = _itemWasSynced.GetOrAdd(absolutePath, true);
        _itemWasSynced[absolutePath] = inSync;
        if (wasSynced != inSync)
            PropagateParentState(absolutePath, inSync);
    }

    public Task RemoveAsync(string absolutePath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearPairAsync(string localRootPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            if (_connectionKey != 0)
            {
                int hr = CfApi.CfDisconnectSyncRoot(_connectionKey);
                if (hr < 0)
                    _logger.LogDebug("CfDisconnectSyncRoot failed: 0x{Hr:X8}", hr);
                _connectionKey = 0;
            }
            _logger.LogInformation("Cloud sync root disconnected: {Path}", _syncRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to disconnect cloud sync root");
        }
        _registered = false;
    }

    // ---------------------------------------------------------------
    // Directory state propagation (reference-counting)
    // ---------------------------------------------------------------

    private void PropagateParentState(string filePath, bool fileIsNowSynced)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);

            while (dir is not null &&
                   dir.StartsWith(_syncRootPath, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(dir, _syncRootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (fileIsNowSynced)
                {
                    var newCount = _dirNotSyncedCount.AddOrUpdate(dir, 0, (_, c) => Math.Max(0, c - 1));
                    if (newCount == 0)
                    {
                        EnsurePlaceholder(dir);
                        SetInSyncState(dir, true, pin: true);
                    }
                }
                else
                {
                    _dirNotSyncedCount.AddOrUpdate(dir, 1, (_, c) => c + 1);
                    EnsurePlaceholder(dir);
                    SetInSyncState(dir, false, pin: false);
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to propagate parent state for '{Path}'", filePath);
        }
    }

    // ---------------------------------------------------------------
    // Cloud Filter Connection
    // ---------------------------------------------------------------

    private const int ERROR_CLOUD_FILE_ALREADY_CONNECTED = unchecked((int)0x80070186);

    private void ConnectSyncRoot()
    {
        var callbackTable = new CfApi.CF_CALLBACK_REGISTRATION[]
        {
            new() { Type = CfApi.CF_CALLBACK_TYPE_NONE, Callback = IntPtr.Zero }
        };

        int hr = CfApi.CfConnectSyncRoot(
            _syncRootPath, callbackTable, IntPtr.Zero,
            CfApi.CF_CONNECT_FLAG_NONE, out _connectionKey);

        if (hr == ERROR_CLOUD_FILE_ALREADY_CONNECTED)
        {
            _logger.LogWarning("Sync root already connected (stale). Unregistering and retrying...");
            try { StorageProviderSyncRootManager.Unregister(_syncRootId); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to unregister stale sync root"); }

            RegisterSyncRootAsync().GetAwaiter().GetResult();

            hr = CfApi.CfConnectSyncRoot(
                _syncRootPath, callbackTable, IntPtr.Zero,
                CfApi.CF_CONNECT_FLAG_NONE, out _connectionKey);
        }

        if (hr < 0)
            throw new InvalidOperationException(
                $"CfConnectSyncRoot failed: 0x{hr:X8} for {_syncRootPath}");
    }

    // ---------------------------------------------------------------
    // Sync Root Registration (WinRT)
    // ---------------------------------------------------------------

    private async Task RegisterSyncRootAsync()
    {
        // Clean up any stale registrations from our provider (e.g. leftover subfolder roots)
        try
        {
            var prefix = $"{_syncRootId.Split('!')[0]}!{_syncRootId.Split('!')[1]}!";
            var existing = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in existing)
            {
                if (root.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Cleaning up existing sync root: {Id}", root.Id);
                    try { StorageProviderSyncRootManager.Unregister(root.Id); }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* No existing roots */ }

        var folder = await StorageFolder.GetFolderFromPathAsync(_syncRootPath);

        var info = new StorageProviderSyncRootInfo
        {
            Id = _syncRootId,
            Path = folder,
            DisplayNameResource = _displayName,
            IconResource = "%SystemRoot%\\system32\\shell32.dll,-16766",
            Version = "1.0",
            HydrationPolicy = StorageProviderHydrationPolicy.AlwaysFull,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime |
                           StorageProviderInSyncPolicy.DirectoryLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            RecycleBinUri = null,
            Context = CryptographicBuffer.ConvertStringToBinary(_syncRootId, BinaryStringEncoding.Utf8),
            ShowSiblingsAsGroup = false,
        };

        StorageProviderSyncRootManager.Register(info);
    }

    // ---------------------------------------------------------------
    // Cloud Filter API: Placeholder + In-Sync State
    // ---------------------------------------------------------------

    private void EnsurePlaceholder(string absolutePath)
    {
        if (!Path.Exists(absolutePath)) return;
        if (CfApi.IsPlaceholder(absolutePath)) return;

        using var handle = OpenFileHandle(absolutePath);
        if (handle is null) return;

        var identity = System.Text.Encoding.UTF8.GetBytes(absolutePath);
        int hr = CfApi.CfConvertToPlaceholder(
            handle.DangerousGetHandle(),
            identity,
            (uint)identity.Length,
            CfApi.CF_CONVERT_FLAG_MARK_IN_SYNC,
            out _,
            IntPtr.Zero);

        if (hr < 0)
            _logger.LogDebug("CfConvertToPlaceholder failed: 0x{Hr:X8} for {Path}", hr, absolutePath);
    }

    /// <summary>
    /// Sets in-sync state and optionally pins the item.
    /// Pinned + in-sync = green checkmark.
    /// Unpinned + in-sync = no visible icon (suppresses sync arrows).
    /// </summary>
    private void SetInSyncState(string absolutePath, bool inSync, bool pin)
    {
        if (!Path.Exists(absolutePath)) return;

        using var handle = OpenFileHandle(absolutePath);
        if (handle is null) return;

        int hr = CfApi.CfSetInSyncState(
            handle.DangerousGetHandle(),
            inSync ? CfApi.CF_IN_SYNC_STATE_IN_SYNC : CfApi.CF_IN_SYNC_STATE_NOT_IN_SYNC,
            CfApi.CF_SET_IN_SYNC_FLAG_NONE,
            IntPtr.Zero);

        if (hr < 0)
            _logger.LogDebug("CfSetInSyncState failed: 0x{Hr:X8} for {Path}", hr, absolutePath);

        if (pin && inSync)
        {
            int pinHr = CfApi.CfSetPinState(
                handle.DangerousGetHandle(),
                CfApi.CF_PIN_STATE_PINNED,
                CfApi.CF_SET_PIN_FLAG_NONE,
                IntPtr.Zero);

            if (pinHr < 0)
                _logger.LogDebug("CfSetPinState failed: 0x{Hr:X8} for {Path}", pinHr, absolutePath);
        }
        else if (!pin)
        {
            // Mark as excluded from sync — may suppress the status icon entirely
            int pinHr = CfApi.CfSetPinState(
                handle.DangerousGetHandle(),
                CfApi.CF_PIN_STATE_EXCLUDED,
                CfApi.CF_SET_PIN_FLAG_NONE,
                IntPtr.Zero);

            if (pinHr < 0)
                _logger.LogDebug("CfSetPinState(EXCLUDED) failed: 0x{Hr:X8} for {Path}", pinHr, absolutePath);
        }
    }

    private static SafeFileHandle? OpenFileHandle(string path)
    {
        var handle = CfApi.CreateFileW(
            path,
            CfApi.FILE_WRITE_ATTRIBUTES,
            CfApi.FILE_SHARE_READ | CfApi.FILE_SHARE_WRITE | CfApi.FILE_SHARE_DELETE,
            IntPtr.Zero,
            CfApi.OPEN_EXISTING,
            CfApi.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid) return null;
        return handle;
    }

    // ---------------------------------------------------------------
    // P/Invoke: cldapi.dll + kernel32.dll
    // ---------------------------------------------------------------

    private static class CfApi
    {
        public const uint CF_CONVERT_FLAG_MARK_IN_SYNC = 0x00000001;

        public const uint CF_IN_SYNC_STATE_NOT_IN_SYNC = 0;
        public const uint CF_IN_SYNC_STATE_IN_SYNC = 1;
        public const uint CF_SET_IN_SYNC_FLAG_NONE = 0;

        public const uint CF_PIN_STATE_UNSPECIFIED = 0;
        public const uint CF_PIN_STATE_PINNED = 1;
        public const uint CF_PIN_STATE_UNPINNED = 2;
        public const uint CF_PIN_STATE_EXCLUDED = 3;
        public const uint CF_SET_PIN_FLAG_NONE = 0;

        public const uint CF_CONNECT_FLAG_NONE = 0;

        public const uint CF_CALLBACK_TYPE_NONE = 0xffffffff;

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_CALLBACK_REGISTRATION
        {
            public uint Type;
            public IntPtr Callback;
        }

        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

        public const uint FILE_WRITE_ATTRIBUTES = 0x100;
        public const uint FILE_SHARE_READ = 0x01;
        public const uint FILE_SHARE_WRITE = 0x02;
        public const uint FILE_SHARE_DELETE = 0x04;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        public static extern int CfConvertToPlaceholder(
            IntPtr fileHandle,
            byte[]? fileIdentity,
            uint fileIdentityLength,
            uint convertFlags,
            out long convertUsn,
            IntPtr overlapped);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        public static extern int CfSetInSyncState(
            IntPtr fileHandle,
            uint inSyncState,
            uint setInSyncFlags,
            out long usn);

        public static int CfSetInSyncState(IntPtr fileHandle, uint inSyncState, uint flags, IntPtr reserved)
            => CfSetInSyncState(fileHandle, inSyncState, flags, out _);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        public static extern int CfSetPinState(
            IntPtr fileHandle,
            uint pinState,
            uint pinFlags,
            IntPtr overlapped);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        public static extern int CfConnectSyncRoot(
            [MarshalAs(UnmanagedType.LPWStr)] string syncRootPath,
            [MarshalAs(UnmanagedType.LPArray)] CF_CALLBACK_REGISTRATION[] callbackTable,
            IntPtr callbackContext,
            uint connectFlags,
            out long connectionKey);

        [DllImport("cldapi.dll")]
        public static extern int CfDisconnectSyncRoot(long connectionKey);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll")]
        public static extern uint GetFileAttributesW([MarshalAs(UnmanagedType.LPWStr)] string lpFileName);

        public static bool IsPlaceholder(string path)
        {
            uint attrs = GetFileAttributesW(path);
            if (attrs == 0xFFFFFFFF) return false;
            return (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }
    }
}
