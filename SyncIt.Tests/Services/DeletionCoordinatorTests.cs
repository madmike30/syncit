using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public sealed class DeletionCoordinatorTests : IAsyncLifetime
{
    private FileStateStore _stateStore = null!;
    private DeletionJournal _journal = null!;
    private DeletionCoordinator _coordinator = null!;
    private Mock<IGoogleDriveService> _mockDrive = null!;
    private SyncConfig _config = null!;
    private SyncPair _pair = null!;

    public async Task InitializeAsync()
    {
        _stateStore = await TestHelpers.CreateInMemoryStateStoreAsync();
        _journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        _config = TestHelpers.CreateTestSyncConfig();
        _config.MaxDeletionsPerCycle = 3;
        _config.DeletionGraceCycles = 2;
        _pair = TestHelpers.CreateTestSyncPair();
        _mockDrive = new Mock<IGoogleDriveService>();

        var validator = new DeletionSafetyValidator(
            _config, _stateStore, _journal, NullLogger<DeletionSafetyValidator>.Instance);
        var trashManager = new LocalTrashManager(
            _config.LocalTrashPath, NullLogger<LocalTrashManager>.Instance);
        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        _coordinator = new DeletionCoordinator(
            _config, validator, _journal, trashManager, _mockDrive.Object,
            _stateStore, atomicWriter, NullLogger<DeletionCoordinator>.Instance);
    }

    public Task DisposeAsync()
    {
        _stateStore.Dispose();
        _journal.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SubmitAsync_UnderThreshold_RecordsWithGracePeriod()
    {
        await _stateStore.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));

        var deletions = new List<DeletionRecord>
        {
            TestHelpers.CreateDeletionRecord(relativePath: "a.txt"),
        };

        var frozen = await _coordinator.SubmitAsync(_pair, deletions);

        frozen.Should().BeFalse();

        var count = await _journal.CountPendingAsync("test0001");
        count.Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_ExceedsThreshold_FreezesDeletions()
    {
        // Create baselines for 5 files
        for (int i = 0; i < 5; i++)
            await _stateStore.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: $"file{i}.txt"));

        var deletions = Enumerable.Range(0, 5)
            .Select(i => TestHelpers.CreateDeletionRecord(relativePath: $"file{i}.txt"))
            .ToList();

        bool freezeEventFired = false;
        _coordinator.DeletionFreezeTriggered += (_, _) => freezeEventFired = true;

        var frozen = await _coordinator.SubmitAsync(_pair, deletions);

        frozen.Should().BeTrue();
        freezeEventFired.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteApprovedAsync_RemoteSide_CallsTrashFile()
    {
        // Set up a record that's ready to execute (0 grace, pending)
        var record = TestHelpers.CreateDeletionRecord(
            side: SyncSide.Remote,
            relativePath: "remote.txt",
            graceCycles: 0);
        record.DriveFileId = "drive-id-123";
        await _journal.RecordAsync(record);

        _mockDrive.Setup(d => d.TrashFileAsync("drive-id-123", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _coordinator.ExecuteApprovedAsync(_pair);

        _mockDrive.Verify(d => d.TrashFileAsync("drive-id-123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApproveFrozenAsync_UnfreezesRecords()
    {
        var record = TestHelpers.CreateDeletionRecord(status: DeletionStatus.Frozen);
        await _journal.RecordAsync(record);

        await _coordinator.ApproveFrozenAsync("test0001");

        // Should now be Pending (unfrozen)
        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync("test0001"))
            frozen.Add(del);
        frozen.Should().BeEmpty();

        var pending = await _journal.CountPendingAsync("test0001");
        pending.Should().Be(1);
    }

    [Fact]
    public async Task CancelFrozenAsync_MarksAsCancelled()
    {
        var record = TestHelpers.CreateDeletionRecord(status: DeletionStatus.Frozen);
        await _journal.RecordAsync(record);

        await _coordinator.CancelFrozenAsync("test0001");

        var frozen = new List<DeletionRecord>();
        await foreach (var del in _journal.GetFrozenAsync("test0001"))
            frozen.Add(del);
        frozen.Should().BeEmpty();

        var pending = await _journal.CountPendingAsync("test0001");
        pending.Should().Be(0); // cancelled, not pending
    }

    [Fact]
    public async Task SubmitAsync_EmptyList_ReturnsFalse()
    {
        var frozen = await _coordinator.SubmitAsync(_pair, Array.Empty<DeletionRecord>());
        frozen.Should().BeFalse();
    }
}
