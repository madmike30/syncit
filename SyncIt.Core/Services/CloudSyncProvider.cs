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
/// This drives the Status column in File Explorer — the modern replacement
/// for legacy IShellIconOverlayIdentifier COM overlays.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class CloudSyncProvider : IOverlayStateStore
{
    private readonly string _syncRootPath;
    private readonly string _syncRootId;
    private readonly ILogger _logger;
    private bool _registered;
    private long _connectionKey;

    // Tracks how many non-synced files exist under each directory.
    // When count drops to 0, the directory is marked as in-sync.
    private readonly ConcurrentDictionary<string, int> _dirNotSyncedCount = new(StringComparer.OrdinalIgnoreCase);

    public CloudSyncProvider(string syncRootPath, string providerId, ILogger logger)
    {
        _syncRootPath = Path.GetFullPath(syncRootPath);
        _logger = logger;

        // Build sync root ID: ProviderId!UserSID!AccountId
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

            // Convert existing files to placeholders and mark synced items.
            // Only items passing the filter get green checkmarks; others stay unmarked.
            MarkAllAsSynced(isSyncedFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register cloud sync root at {Path}", _syncRootPath);
        }
    }

    private void MarkAllAsSynced(Func<string, bool>? isSyncedFilter)
    {
        int converted = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_syncRootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_syncRootPath, dir).Replace('\\', '/');
                    if (isSyncedFilter is not null && !isSyncedFilter(relativePath))
                    {
                        skipped++;
                        continue;
                    }
                    EnsurePlaceholder(dir);
                    SetInSyncState(dir, true);
                    converted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "Failed to convert directory {Path} to placeholder", dir);
                }
            }

            foreach (var file in Directory.EnumerateFiles(_syncRootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_syncRootPath, file).Replace('\\', '/');
                    if (isSyncedFilter is not null && !isSyncedFilter(relativePath))
                    {
                        skipped++;
                        continue;
                    }
                    EnsurePlaceholder(file);
                    SetInSyncState(file, true);
                    converted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "Failed to convert {Path} to placeholder", file);
                }
            }
            _logger.LogInformation("Marked {Synced} items as synced, {Skipped} skipped ({Failed} failed)",
                converted, skipped, failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning sync root for initial placeholder conversion");
        }
    }

    public Task SetStateAsync(string absolutePath, OverlaySyncState state, CancellationToken ct = default)
    {
        if (!_registered) return Task.CompletedTask;
        try
        {
            bool inSync = state == OverlaySyncState.Synced;
            EnsurePlaceholder(absolutePath);
            SetInSyncState(absolutePath, inSync);
            PropagateParentState(absolutePath, inSync);
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
                SetInSyncState(path, inSync);
                PropagateParentState(path, inSync);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set cloud state for '{Path}'", path);
            }
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string absolutePath, CancellationToken ct = default)
    {
        // File is being deleted — nothing to do with cloud state
        return Task.CompletedTask;
    }

    public Task ClearPairAsync(string localRootPath, CancellationToken ct = default)
    {
        // Pair disabled — could unregister sync root, but we'll let Dispose handle it
        return Task.CompletedTask;
    }

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
            // Don't unregister — we want the sync root to persist across restarts.
            // Windows will show the folder as "not connected" when the service is stopped,
            // and reconnect when it starts again.
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

    /// <summary>
    /// Walk up from the file to the sync root, updating parent directory in-sync state.
    /// Uses a reference-counting approach: each directory tracks how many non-synced
    /// children it has. When the count reaches 0, the directory is marked as in-sync.
    /// </summary>
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
                    // File became synced — decrement parent's not-synced count
                    var newCount = _dirNotSyncedCount.AddOrUpdate(dir, 0, (_, c) => Math.Max(0, c - 1));
                    if (newCount == 0)
                    {
                        EnsurePlaceholder(dir);
                        SetInSyncState(dir, true);
                    }
                }
                else
                {
                    // File became not-synced — increment parent's not-synced count
                    _dirNotSyncedCount.AddOrUpdate(dir, 1, (_, c) => c + 1);
                    EnsurePlaceholder(dir);
                    SetInSyncState(dir, false);
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
        // Empty callback table — terminated by CF_CALLBACK_TYPE_NONE sentinel.
        // Since we use HydrationPolicy.Full, all files are already local and
        // no hydration/dehydration callbacks are needed. We just need to connect
        // so Windows knows the provider is running and allows file access.
        var callbackTable = new CfApi.CF_CALLBACK_REGISTRATION[]
        {
            new() { Type = CfApi.CF_CALLBACK_TYPE_NONE, Callback = IntPtr.Zero }
        };

        int hr = CfApi.CfConnectSyncRoot(
            _syncRootPath,
            callbackTable,
            IntPtr.Zero,
            CfApi.CF_CONNECT_FLAG_NONE,
            out _connectionKey);

        // If a previous process was killed without disconnecting, the sync root
        // is still connected with a stale key. Unregister to clear the dead
        // connection, re-register, and retry.
        if (hr == ERROR_CLOUD_FILE_ALREADY_CONNECTED)
        {
            _logger.LogWarning("Sync root already connected (stale from previous crash). Unregistering and retrying...");
            try
            {
                StorageProviderSyncRootManager.Unregister(_syncRootId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to unregister stale sync root");
            }

            // Re-register (Unregister clears the dead connection)
            RegisterSyncRootAsync().GetAwaiter().GetResult();

            hr = CfApi.CfConnectSyncRoot(
                _syncRootPath,
                callbackTable,
                IntPtr.Zero,
                CfApi.CF_CONNECT_FLAG_NONE,
                out _connectionKey);
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
        // Check if already registered
        if (IsAlreadyRegistered()) return;

        var folder = await StorageFolder.GetFolderFromPathAsync(_syncRootPath);

        var info = new StorageProviderSyncRootInfo
        {
            Id = _syncRootId,
            Path = folder,
            DisplayNameResource = "SyncIt",
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

    private bool IsAlreadyRegistered()
    {
        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in roots)
            {
                if (root.Id == _syncRootId) return true;
            }
        }
        catch { /* Not registered */ }
        return false;
    }

    // ---------------------------------------------------------------
    // Cloud Filter API: Placeholder + In-Sync State
    // ---------------------------------------------------------------

    private void EnsurePlaceholder(string absolutePath)
    {
        // Path.Exists works for both files and directories
        if (!Path.Exists(absolutePath)) return;

        // Check if already a placeholder
        if (CfApi.IsPlaceholder(absolutePath)) return;

        // Convert to placeholder (keeps content, adds reparse point metadata)
        // FILE_FLAG_BACKUP_SEMANTICS in OpenFileHandle allows opening directories too
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

    private void SetInSyncState(string absolutePath, bool inSync)
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

        // Pin as "always available on this device" so it shows a green checkmark
        // instead of a blue cloud ("online-only")
        if (inSync)
        {
            int pinHr = CfApi.CfSetPinState(
                handle.DangerousGetHandle(),
                CfApi.CF_PIN_STATE_PINNED,
                CfApi.CF_SET_PIN_FLAG_NONE,
                IntPtr.Zero);

            if (pinHr < 0)
                _logger.LogDebug("CfSetPinState failed: 0x{Hr:X8} for {Path}", pinHr, absolutePath);
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
        // CfConvertToPlaceholder flags
        public const uint CF_CONVERT_FLAG_MARK_IN_SYNC = 0x00000001;

        // CfSetInSyncState values
        public const uint CF_IN_SYNC_STATE_NOT_IN_SYNC = 0;
        public const uint CF_IN_SYNC_STATE_IN_SYNC = 1;

        // CfSetInSyncState flags
        public const uint CF_SET_IN_SYNC_FLAG_NONE = 0;

        // CfSetPinState values (from cfapi.h)
        public const uint CF_PIN_STATE_PINNED = 1;
        public const uint CF_PIN_STATE_UNPINNED = 2;
        public const uint CF_SET_PIN_FLAG_NONE = 0;

        // CfConnectSyncRoot flags
        public const uint CF_CONNECT_FLAG_NONE = 0;

        // CF_CALLBACK_TYPE sentinel (terminates callback registration array)
        // Must be 0xffffffff per cfapi.h — wrong value causes AV as API reads past buffer
        public const uint CF_CALLBACK_TYPE_NONE = 0xffffffff;

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_CALLBACK_REGISTRATION
        {
            public uint Type;
            public IntPtr Callback;
        }

        // File attributes for placeholder check
        public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

        // CreateFileW constants
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

        // Overload without usn out param
        public static int CfSetInSyncState(IntPtr fileHandle, uint inSyncState, uint flags, IntPtr reserved)
        {
            return CfSetInSyncState(fileHandle, inSyncState, flags, out _);
        }

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

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        public static extern int CfUnregisterSyncRoot(string syncRootPath);

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
            // Cloud filter placeholders have a reparse point (IO_REPARSE_TAG_CLOUD = 0x9000601a).
            // For AlwaysFull providers, RECALL_ON_DATA_ACCESS is NOT set — check ReparsePoint instead.
            return (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }

        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    }
}
