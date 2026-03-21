using System.Diagnostics;
using FluentAssertions;
using SyncIt.Core.IO;

namespace SyncIt.Tests.IO;

public sealed class ThrottledStreamTests
{
    [Fact]
    public async Task Read_WithLimit_ThrottlesThroughput()
    {
        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        using var inner = new MemoryStream(data);

        // Limit to 2048 bytes/sec — reading 4096 bytes should take ~2 seconds
        using var throttled = new ThrottledStream(inner, 2048);
        var buffer = new byte[4096];
        var sw = Stopwatch.StartNew();

        int totalRead = 0;
        while (totalRead < data.Length)
        {
            int read = await throttled.ReadAsync(buffer.AsMemory(totalRead, data.Length - totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        sw.Stop();
        totalRead.Should().Be(4096);
        sw.ElapsedMilliseconds.Should().BeGreaterThan(500, "throttling should slow the read");
    }

    [Fact]
    public async Task Write_WithLimit_ThrottlesThroughput()
    {
        using var inner = new MemoryStream();
        using var throttled = new ThrottledStream(inner, 2048);

        var data = new byte[4096];
        Random.Shared.NextBytes(data);

        var sw = Stopwatch.StartNew();
        await throttled.WriteAsync(data);
        sw.Stop();

        inner.Length.Should().Be(4096);
        sw.ElapsedMilliseconds.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task Read_Unlimited_NoSignificantDelay()
    {
        var data = new byte[1024 * 1024]; // 1MB
        using var inner = new MemoryStream(data);
        using var throttled = new ThrottledStream(inner, 0); // unlimited

        var buffer = new byte[data.Length];
        var sw = Stopwatch.StartNew();
        await throttled.ReadAsync(buffer);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500, "unlimited should not add delay");
    }

    [Fact]
    public void DynamicRateChange_PropertyUpdates()
    {
        using var inner = new MemoryStream();
        using var throttled = new ThrottledStream(inner, 1024);

        throttled.MaxBytesPerSecond.Should().Be(1024);

        throttled.MaxBytesPerSecond = 2048;
        throttled.MaxBytesPerSecond.Should().Be(2048);

        throttled.MaxBytesPerSecond = 0;
        throttled.MaxBytesPerSecond.Should().Be(0);
    }

    [Fact]
    public void Properties_DelegateToInner()
    {
        var data = new byte[100];
        using var inner = new MemoryStream(data);
        using var throttled = new ThrottledStream(inner, 0);

        throttled.CanRead.Should().Be(inner.CanRead);
        throttled.CanWrite.Should().Be(inner.CanWrite);
        throttled.CanSeek.Should().Be(inner.CanSeek);
        throttled.Length.Should().Be(100);
        throttled.Position.Should().Be(0);

        throttled.Position = 50;
        throttled.Position.Should().Be(50);
        inner.Position.Should().Be(50);
    }
}
