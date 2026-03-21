namespace SyncIt.Core.IO;

/// <summary>
/// Stream wrapper that limits read/write throughput using a token-bucket algorithm.
/// Set MaxBytesPerSecond to 0 for unlimited throughput.
/// The rate can be changed dynamically at any time.
/// </summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private long _bytesInCurrentWindow;
    private long _windowStartTicks;

    /// <summary>Maximum bytes per second. 0 = unlimited.</summary>
    public long MaxBytesPerSecond { get; set; }

    public ThrottledStream(Stream inner, long maxBytesPerSecond)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        MaxBytesPerSecond = maxBytesPerSecond;
        _windowStartTicks = Environment.TickCount64;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        Throttle(count).GetAwaiter().GetResult();
        return _inner.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await Throttle(count, ct);
        return await _inner.ReadAsync(buffer, offset, count, ct);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await Throttle(buffer.Length, ct);
        return await _inner.ReadAsync(buffer, ct);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Throttle(count).GetAwaiter().GetResult();
        _inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await Throttle(count, ct);
        await _inner.WriteAsync(buffer, offset, count, ct);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await Throttle(buffer.Length, ct);
        await _inner.WriteAsync(buffer, ct);
    }

    private async ValueTask Throttle(int requestedBytes, CancellationToken ct = default)
    {
        var limit = MaxBytesPerSecond;
        if (limit <= 0) return;

        var now = Environment.TickCount64;
        var elapsed = now - _windowStartTicks;

        if (elapsed >= 1000)
        {
            // Start a new window
            _windowStartTicks = now;
            _bytesInCurrentWindow = 0;
        }

        _bytesInCurrentWindow += requestedBytes;

        if (_bytesInCurrentWindow > limit)
        {
            // Calculate how long to wait before the next window
            var remainingMs = 1000 - (int)(now - _windowStartTicks);
            if (remainingMs > 0)
            {
                await Task.Delay(remainingMs, ct);
                _windowStartTicks = Environment.TickCount64;
                _bytesInCurrentWindow = 0;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
