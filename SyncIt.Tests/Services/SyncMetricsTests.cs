using FluentAssertions;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Services;

public class SyncMetricsTests
{
    [Fact]
    public void RecordUpload_AccumulatesBytes()
    {
        var m = new SyncMetrics();
        m.RecordUpload(100);
        m.RecordUpload(200);
        m.TotalBytesUploaded.Should().Be(300);
    }

    [Fact]
    public void RecordDownload_AccumulatesBytes()
    {
        var m = new SyncMetrics();
        m.RecordDownload(500);
        m.TotalBytesDownloaded.Should().Be(500);
    }

    [Fact]
    public void RecordError_Increments()
    {
        var m = new SyncMetrics();
        m.RecordError();
        m.RecordError();
        m.TotalErrors.Should().Be(2);
    }

    [Fact]
    public void StartEndCycle_TracksCount()
    {
        var m = new SyncMetrics();
        m.StartCycle();
        m.EndCycle();
        m.TotalSyncCycles.Should().Be(1);
        m.LastCycleDurationMs.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void TakeSnapshot_ReturnsAllValues()
    {
        var m = new SyncMetrics();
        m.RecordUpload(1024);
        m.RecordDownload(2048);
        m.RecordApiCall();
        m.RecordError();
        m.StartCycle();
        m.EndCycle();

        var snap = m.TakeSnapshot();
        snap.TotalBytesUploaded.Should().Be(1024);
        snap.TotalBytesDownloaded.Should().Be(2048);
        snap.TotalApiCalls.Should().Be(1);
        snap.TotalErrors.Should().Be(1);
        snap.TotalSyncCycles.Should().Be(1);
    }

    [Fact]
    public void ThreadSafety_ConcurrentRecording()
    {
        var m = new SyncMetrics();
        Parallel.For(0, 1000, _ =>
        {
            m.RecordUpload(10);
            m.RecordDownload(20);
            m.RecordApiCall();
        });
        m.TotalBytesUploaded.Should().Be(10000);
        m.TotalBytesDownloaded.Should().Be(20000);
        m.TotalApiCalls.Should().Be(1000);
    }
}
