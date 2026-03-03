using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Models;

namespace SyncIt.Core.Ipc;

/// <summary>
/// Named pipe server (service side).
/// Accepts connections from the tray app, pushes status updates, and receives commands.
///
/// Security: the pipe is restricted to the current Windows user SID.
/// No other processes on the machine can connect.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcServer : IAsyncDisposable
{
    private readonly SyncConfig _config;
    private readonly ILogger<IpcServer> _logger;
    private readonly CancellationTokenSource _cts = new();

    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Raised when a command arrives from the tray
    public event Func<IpcMessage, Task>? CommandReceived;

    public IpcServer(SyncConfig config, ILogger<IpcServer> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Start accepting connections in the background.</summary>
    public void Start()
    {
        _ = Task.Run(AcceptLoopAsync, _cts.Token);
        _logger.LogInformation("IPC server started on pipe '{PipeName}'", _config.PipeName);
    }

    /// <summary>Push a message to the connected tray app.</summary>
    public async Task SendAsync(IpcMessage message)
    {
        if (_writer is null) return;

        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var json = IpcSerializer.Serialize(message);
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IPC write failed — tray may be disconnected.");
            _writer = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Accept loop
    // -------------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        // Create the pipe once; reuse it via Disconnect() between connections.
        // This avoids the "all pipe instances are busy" race when re-creating
        // a new OS handle before the previous one has been fully released.
        NamedPipeServerStream? pipe = null;
        try
        {
            pipe = CreatePipe();
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _logger.LogDebug("IPC waiting for tray connection...");
                    await pipe.WaitForConnectionAsync(_cts.Token);
                    _logger.LogInformation("Tray app connected via IPC.");

                    await HandleConnectionAsync(pipe, _cts.Token);

                    // Reset for next client — keeps the OS handle, clears the connection.
                    pipe.Disconnect();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPC server error — recreating pipe.");
                    pipe.Dispose();
                    pipe = null;
                    await Task.Delay(2000, _cts.Token);
                    pipe = CreatePipe();
                }
            }
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // Pipe lifetime is managed by AcceptLoopAsync — do NOT dispose here.
        _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = false };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;  // client disconnected

                var msg = IpcSerializer.Deserialize(line);
                if (msg is null) continue;

                _logger.LogDebug("IPC received: {Type}", msg.Type);

                if (CommandReceived is not null)
                    await CommandReceived(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IPC connection closed.");
        }
        finally
        {
            // Dispose the StreamWriter to release its internal buffer
            try { _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;
            _logger.LogInformation("Tray app disconnected from IPC.");
        }
    }

    // -------------------------------------------------------------------------
    // Pipe factory with security
    // -------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _config.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize:  4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
