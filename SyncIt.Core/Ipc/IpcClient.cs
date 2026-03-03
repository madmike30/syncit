using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Models;

namespace SyncIt.Core.Ipc;

/// <summary>
/// Named pipe client (tray app side).
/// Connects to the service pipe, reads status push messages, and sends commands.
/// Auto-reconnects if the service restarts.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger<IpcClient> _logger;
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Raised when the service pushes a message (StatusUpdate, Error, etc.).</summary>
    public event Action<IpcMessage>? MessageReceived;

    /// <summary>True while connected to the service pipe.</summary>
    public bool IsConnected => _pipe?.IsConnected == true;

    public IpcClient(string pipeName, ILogger<IpcClient> logger)
    {
        _pipeName = pipeName;
        _logger   = logger;
    }

    /// <summary>Start connecting and reading in the background.</summary>
    public void Start()
    {
        _ = Task.Run(ConnectLoopAsync, _cts.Token);
    }

    /// <summary>Send a command to the service.</summary>
    public async Task SendAsync(IpcMessage message)
    {
        if (_writer is null)
        {
            _logger.LogDebug("IPC not connected — command {Type} dropped.", message.Type);
            return;
        }

        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var json = IpcSerializer.Serialize(message);
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IPC send failed.");
            _writer = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Connect + read loop
    // -------------------------------------------------------------------------

    private async Task ConnectLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _logger.LogDebug("Connecting to service pipe '{PipeName}'...", _pipeName);

                // Try to connect with a timeout; service may not be started yet
                await _pipe.ConnectAsync(5000, _cts.Token);
                _logger.LogInformation("Connected to service pipe.");

                _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = false };
                using var reader = new StreamReader(_pipe, leaveOpen: true);

                // Request an immediate status update
                await SendAsync(IpcSerializer.Build(IpcMessageType.RequestStatus));

                // Read loop
                while (!_cts.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;

                    var msg = IpcSerializer.Deserialize(line);
                    if (msg is not null)
                    {
                        _logger.LogDebug("IPC received: {Type}", msg.Type);
                        MessageReceived?.Invoke(msg);
                    }
                }

                _logger.LogWarning("IPC connection lost. Reconnecting...");
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Pipe connect timeout — service may not be running yet.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IPC connection error.");
            }
            finally
            {
                _writer = null;
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }

            // Wait before reconnect attempt
            if (!_cts.IsCancellationRequested)
                await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _writeLock.Dispose();
        _cts.Dispose();
        _pipe?.Dispose();
    }
}
