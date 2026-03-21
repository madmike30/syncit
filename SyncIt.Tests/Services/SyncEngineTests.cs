using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public sealed class SyncEngineTests : IAsyncLifetime
{
    private SyncEngine _engine = null!;
    private SyncPair _pair = null!;
    private SyncConfig _config = null!;
    private Mock<IGoogleDriveService> _mockDrive = null!;
    private Mock<IFileStateStore> _mockStateStore = null!;
    private Mock<IConflictResolver> _mockConflictResolver = null!;
    private Mock<IDeletionCoordinator> _mockDeletionCoordinator = null!;
    private Mock<IGlobFilter> _mockGlobFilter = null!;
    private Mock<IChangeQueue> _mockChangeQueue = null!;

    public async Task InitializeAsync()
    {
        _pair = TestHelpers.CreateTestSyncPair();
        _config = TestHelpers.CreateTestSyncConfig();
        _mockDrive = new Mock<IGoogleDriveService>();
        _mockStateStore = new Mock<IFileStateStore>();
        _mockConflictResolver = new Mock<IConflictResolver>();
        _mockDeletionCoordinator = new Mock<IDeletionCoordinator>();
        _mockGlobFilter = new Mock<IGlobFilter>();
        _mockChangeQueue = new Mock<IChangeQueue>();

        // DeletionSafetyValidator is a concrete class — we need to create a real one
        // with mocked dependencies to pass to SyncEngine
        var stateStore = await TestHelpers.CreateInMemoryStateStoreAsync();
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safetyValidator = new DeletionSafetyValidator(
            _config, stateStore, journal, NullLogger<DeletionSafetyValidator>.Instance);

        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        _engine = new SyncEngine(
            _pair, _config, _mockDrive.Object, _mockStateStore.Object,
            _mockConflictResolver.Object, _mockDeletionCoordinator.Object,
            _mockGlobFilter.Object, _mockChangeQueue.Object, safetyValidator,
            atomicWriter, NullLogger<SyncEngine>.Instance);
    }

    public Task DisposeAsync()
    {
        return _engine.DisposeAsync().AsTask();
    }

    [Fact]
    public void GetStatus_ReturnsCorrectPairId()
    {
        var status = _engine.GetStatus();

        status.SyncPairId.Should().Be(_pair.Id);
        status.LocalPath.Should().Be(_pair.LocalPath);
        status.RemoteFolderName.Should().Be(_pair.RemoteFolderName);
    }

    [Fact]
    public void Pause_SetsStatePaused()
    {
        _engine.Pause();
        var status = _engine.GetStatus();

        status.State.Should().Be(SyncState.Paused);
    }

    [Fact]
    public void Resume_AfterPause_SetsStateIdle()
    {
        _engine.Pause();
        _engine.Resume();

        var status = _engine.GetStatus();
        status.State.Should().Be(SyncState.Idle);
    }

    [Fact]
    public async Task StartAsync_SafetyCheckFails_SetsSafetyHalt()
    {
        // Set the local path to a non-existent directory so startup check fails
        _pair.LocalPath = @"C:\definitely\does\not\exist\syncit-test";

        // Also set up the state store to return a baseline count > 0 to trigger the safety check
        _mockStateStore.Setup(s => s.CountRecordsAsync(_pair.Id, SyncSide.Local))
            .ReturnsAsync(10);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // StartAsync should handle the safety failure gracefully
        await _engine.StartAsync(_pair, cts.Token);

        var status = _engine.GetStatus();
        status.State.Should().Be(SyncState.SafetyHalt);
    }

    [Fact]
    public void TransferTracker_IsAccessible()
    {
        _engine.TransferTracker.Should().NotBeNull();
        _engine.TransferTracker.ActiveTransfers.Should().BeEmpty();
    }

    [Fact]
    public void StatusChanged_EventFiresOnPause()
    {
        SyncPairStatus? receivedStatus = null;
        _engine.StatusChanged += status => receivedStatus = status;

        _engine.Pause();

        receivedStatus.Should().NotBeNull();
        receivedStatus!.State.Should().Be(SyncState.Paused);
    }

    [Fact]
    public void StatusChanged_EventFiresOnResume()
    {
        var statuses = new List<SyncPairStatus>();
        _engine.StatusChanged += status => statuses.Add(status);

        _engine.Pause();
        _engine.Resume();

        statuses.Should().HaveCountGreaterThanOrEqualTo(2);
        statuses.Last().State.Should().Be(SyncState.Idle);
    }
}
