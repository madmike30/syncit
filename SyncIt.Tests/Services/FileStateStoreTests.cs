using FluentAssertions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public sealed class FileStateStoreTests : IAsyncLifetime
{
    private FileStateStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await TestHelpers.CreateInMemoryStateStoreAsync();
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpsertRecord_ThenGetRecord_ReturnsCorrectData()
    {
        var record = TestHelpers.CreateFileRecord(relativePath: "docs/report.txt");
        await _store.UpsertRecordAsync(record);

        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "docs/report.txt");

        result.Should().NotBeNull();
        result!.RelativePath.Should().Be("docs/report.txt");
        result.SizeBytes.Should().Be(1024);
        result.ContentHash.Should().Be("abc123");
    }

    [Fact]
    public async Task UpsertRecord_SameKey_OverwritesPreviousValues()
    {
        var record1 = TestHelpers.CreateFileRecord(contentHash: "hash1");
        await _store.UpsertRecordAsync(record1);

        var record2 = TestHelpers.CreateFileRecord(contentHash: "hash2");
        await _store.UpsertRecordAsync(record2);

        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "docs/file.txt");
        result!.ContentHash.Should().Be("hash2");
    }

    [Fact]
    public async Task MarkDeleted_SetsIsDeletedTrue()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord());
        await _store.MarkDeletedAsync("test0001", SyncSide.Local, "docs/file.txt");

        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "docs/file.txt");
        result!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveRecord_DeletesFromDatabase()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord());
        await _store.RemoveRecordAsync("test0001", SyncSide.Local, "docs/file.txt");

        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "docs/file.txt");
        result.Should().BeNull();
    }

    [Fact]
    public async Task StreamRecords_ReturnsAllNonDeletedRecords()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "b.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "c.txt"));

        var records = new List<FileRecord>();
        await foreach (var r in _store.StreamRecordsAsync("test0001", SyncSide.Local))
            records.Add(r);

        records.Should().HaveCount(3);
    }

    [Fact]
    public async Task StreamRecords_ExcludesDeletedRecords()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "b.txt"));
        await _store.MarkDeletedAsync("test0001", SyncSide.Local, "b.txt");

        var records = new List<FileRecord>();
        await foreach (var r in _store.StreamRecordsAsync("test0001", SyncSide.Local))
            records.Add(r);

        records.Should().HaveCount(1);
        records[0].RelativePath.Should().Be("a.txt");
    }

    [Fact]
    public async Task CountRecords_ReturnsCorrectCount()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "b.txt"));

        var count = await _store.CountRecordsAsync("test0001", SyncSide.Local);
        count.Should().Be(2);
    }

    [Fact]
    public async Task CountRecords_ExcludesDeleted()
    {
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "b.txt"));
        await _store.MarkDeletedAsync("test0001", SyncSide.Local, "b.txt");

        var count = await _store.CountRecordsAsync("test0001", SyncSide.Local);
        count.Should().Be(1);
    }

    [Fact]
    public async Task PageToken_SaveAndRetrieve_RoundTrips()
    {
        await _store.SavePageTokenAsync("test0001", "token-abc-123");
        var token = await _store.GetPageTokenAsync("test0001");
        token.Should().Be("token-abc-123");
    }

    [Fact]
    public async Task PageToken_NonExistentPair_ReturnsNull()
    {
        var token = await _store.GetPageTokenAsync("nonexistent");
        token.Should().BeNull();
    }

    [Fact]
    public async Task LastSyncTime_SaveAndRetrieve_RoundTrips()
    {
        var syncTime = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        await _store.SetLastSyncTimeAsync("test0001", syncTime);

        var result = await _store.GetLastSyncTimeAsync("test0001");
        result.Should().Be(syncTime);
    }

    [Fact]
    public async Task UpdateMtime_OnlyChangesMtime()
    {
        var original = TestHelpers.CreateFileRecord(contentHash: "keep-this");
        await _store.UpsertRecordAsync(original);

        var newTime = DateTime.UtcNow.AddHours(1);
        await _store.UpdateMtimeAsync("test0001", SyncSide.Local, "docs/file.txt", newTime);

        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "docs/file.txt");
        result!.ModifiedUtc.Should().BeCloseTo(newTime, TimeSpan.FromSeconds(1));
        result.ContentHash.Should().Be("keep-this");
    }

    [Fact]
    public async Task GetRecord_NonExistentPath_ReturnsNull()
    {
        var result = await _store.GetRecordAsync("test0001", SyncSide.Local, "nonexistent.txt");
        result.Should().BeNull();
    }

    [Fact]
    public async Task PendingDeletionCycles_SaveAndRetrieve()
    {
        await _store.SetPendingDeletionCyclesAsync("test0001", 5);
        var cycles = await _store.GetPendingDeletionCyclesAsync("test0001");
        cycles.Should().Be(5);
    }
}
