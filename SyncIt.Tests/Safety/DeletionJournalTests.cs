using FluentAssertions;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Safety;

public sealed class DeletionJournalTests : IAsyncLifetime
{
    private DeletionJournal _journal = null!;

    public async Task InitializeAsync()
    {
        _journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
    }

    public Task DisposeAsync()
    {
        _journal.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordAsync_InsertsRecord()
    {
        var record = TestHelpers.CreateDeletionRecord(relativePath: "test.txt");
        await _journal.RecordAsync(record);

        var count = await _journal.CountPendingAsync("test0001");
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        var record = TestHelpers.CreateDeletionRecord();
        await _journal.RecordAsync(record);

        await _journal.UpdateStatusAsync(record.Id, DeletionStatus.Executed, DateTime.UtcNow);

        // Executed records are not "pending"
        var count = await _journal.CountPendingAsync("test0001");
        count.Should().Be(0);
    }

    [Fact]
    public async Task FreezeAllPendingAsync_FreezesPendingRecordsOnly()
    {
        var r1 = TestHelpers.CreateDeletionRecord(relativePath: "a.txt");
        var r2 = TestHelpers.CreateDeletionRecord(relativePath: "b.txt");
        var r3 = TestHelpers.CreateDeletionRecord(relativePath: "c.txt", status: DeletionStatus.Executed);

        await _journal.RecordAsync(r1);
        await _journal.RecordAsync(r2);
        await _journal.RecordAsync(r3);

        var frozenCount = await _journal.FreezeAllPendingAsync("test0001");

        frozenCount.Should().Be(2);

        // Frozen records show up in GetFrozenAsync
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync("test0001"))
            frozen.Add(del);
        frozen.Should().HaveCount(2);
    }

    [Fact]
    public async Task DecrementAllPendingGraceCyclesAsync_TicksDown()
    {
        var record = TestHelpers.CreateDeletionRecord(graceCycles: 3);
        await _journal.RecordAsync(record);

        await _journal.DecrementAllPendingGraceCyclesAsync("test0001");

        // Record should now have grace_cycles = 2; NOT ready to execute yet
        var ready = new List<DeletionRecord>();
        await foreach (var del in _journal.GetReadyToExecuteAsync("test0001"))
            ready.Add(del);
        ready.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReadyToExecuteAsync_ReturnsOnlyZeroGracePending()
    {
        var readyRecord = TestHelpers.CreateDeletionRecord(relativePath: "ready.txt", graceCycles: 0);
        var pendingRecord = TestHelpers.CreateDeletionRecord(relativePath: "pending.txt", graceCycles: 2);

        await _journal.RecordAsync(readyRecord);
        await _journal.RecordAsync(pendingRecord);

        var ready = new List<DeletionRecord>();
        await foreach (var del in _journal.GetReadyToExecuteAsync("test0001"))
            ready.Add(del);

        ready.Should().HaveCount(1);
        ready[0].RelativePath.Should().Be("ready.txt");
    }

    [Fact]
    public async Task CountPendingAsync_CountsBothPendingAndFrozen()
    {
        var r1 = TestHelpers.CreateDeletionRecord(relativePath: "a.txt");
        var r2 = TestHelpers.CreateDeletionRecord(relativePath: "b.txt", status: DeletionStatus.Frozen);

        await _journal.RecordAsync(r1);
        await _journal.RecordAsync(r2);

        var count = await _journal.CountPendingAsync("test0001");
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetFrozenAsync_ReturnsOnlyFrozenRecords()
    {
        var r1 = TestHelpers.CreateDeletionRecord(relativePath: "a.txt", status: DeletionStatus.Pending);
        var r2 = TestHelpers.CreateDeletionRecord(relativePath: "b.txt", status: DeletionStatus.Frozen);

        await _journal.RecordAsync(r1);
        await _journal.RecordAsync(r2);

        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync("test0001"))
            frozen.Add(del);

        frozen.Should().HaveCount(1);
        frozen[0].RelativePath.Should().Be("b.txt");
    }

    [Fact]
    public async Task StatusTransitions_PendingToFrozenToExecuted()
    {
        var record = TestHelpers.CreateDeletionRecord();
        await _journal.RecordAsync(record);

        // Pending → Frozen
        await _journal.FreezeAllPendingAsync("test0001");
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync("test0001"))
            frozen.Add(del);
        frozen.Should().HaveCount(1);

        // Frozen → back to Pending (approved by user, 0 grace)
        await _journal.UpdateStatusAsync(record.Id, DeletionStatus.Pending);

        // Pending → Executed
        await _journal.UpdateStatusAsync(record.Id, DeletionStatus.Executed, DateTime.UtcNow, "/trash/path");

        var finalCount = await _journal.CountPendingAsync("test0001");
        finalCount.Should().Be(0);
    }

    [Fact]
    public async Task DecrementGraceCycleAsync_DecrementsSpecificRecord()
    {
        var record = TestHelpers.CreateDeletionRecord(graceCycles: 1);
        await _journal.RecordAsync(record);

        await _journal.DecrementGraceCycleAsync(record.Id);

        // Now should be ready to execute (grace_cycles = 0)
        var ready = new List<DeletionRecord>();
        await foreach (var del in _journal.GetReadyToExecuteAsync("test0001"))
            ready.Add(del);
        ready.Should().HaveCount(1);
    }
}
