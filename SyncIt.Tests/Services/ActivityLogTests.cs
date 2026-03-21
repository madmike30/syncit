using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Services;

public class ActivityLogTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ActivityLog _log;

    public ActivityLogTests()
    {
        var name = $"activitylog_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
        _keepAlive.Open();
        _log = new ActivityLog(name, _keepAlive, NullLogger<ActivityLog>.Instance);
        _log.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RecordAndGetRecent_RoundTrips()
    {
        var record = new ActivityRecord
        {
            SyncPairId = "pair1",
            Type = ActivityType.Upload,
            RelativePath = "test.txt",
            SizeBytes = 1024,
            Details = "uploaded ok",
        };

        await _log.RecordAsync(record);
        var results = await _log.GetRecentAsync();

        results.Should().HaveCount(1);
        results[0].RelativePath.Should().Be("test.txt");
        results[0].Type.Should().Be(ActivityType.Upload);
        results[0].SizeBytes.Should().Be(1024);
    }

    [Fact]
    public async Task GetRecent_FilterBySyncPairId()
    {
        await _log.RecordAsync(new ActivityRecord { SyncPairId = "p1", Type = ActivityType.Upload, RelativePath = "a.txt" });
        await _log.RecordAsync(new ActivityRecord { SyncPairId = "p2", Type = ActivityType.Download, RelativePath = "b.txt" });

        var results = await _log.GetRecentAsync(syncPairId: "p1");
        results.Should().HaveCount(1);
        results[0].SyncPairId.Should().Be("p1");
    }

    [Fact]
    public async Task GetRecent_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            await _log.RecordAsync(new ActivityRecord { SyncPairId = "p1", Type = ActivityType.Upload, RelativePath = $"file{i}.txt" });

        var results = await _log.GetRecentAsync(limit: 3);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Prune_RemovesOldRecords()
    {
        await _log.RecordAsync(new ActivityRecord
        {
            SyncPairId = "p1",
            Type = ActivityType.Upload,
            RelativePath = "old.txt",
            TimestampUtc = DateTime.UtcNow.AddDays(-60),
        });
        await _log.RecordAsync(new ActivityRecord
        {
            SyncPairId = "p1",
            Type = ActivityType.Upload,
            RelativePath = "new.txt",
        });

        await _log.PruneAsync(maxAgeDays: 30);

        var results = await _log.GetRecentAsync();
        results.Should().HaveCount(1);
        results[0].RelativePath.Should().Be("new.txt");
    }

    public void Dispose()
    {
        _log.Dispose();
        _keepAlive.Dispose();
    }
}
