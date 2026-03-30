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

    [Fact]
    public async Task StartAsync_ReconcileFails_ClearsPendingChangesAndStopsWatcher()
    {
        using var tempDir = new TempDirectory();
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = tempDir.Path;

        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "new.txt"), "hello");

        var config = TestHelpers.CreateTestSyncConfig();
        var drive = new Mock<IGoogleDriveService>();
        var stateStore = new Mock<IFileStateStore>();
        var conflictResolver = new Mock<IConflictResolver>();
        var deletionCoordinator = new Mock<IDeletionCoordinator>();
        var globFilter = new Mock<IGlobFilter>();
        var changeQueue = new ChangeQueue();

        var baselineStore = await TestHelpers.CreateInMemoryStateStoreAsync();
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safetyValidator = new DeletionSafetyValidator(
            config, baselineStore, journal, NullLogger<DeletionSafetyValidator>.Instance);
        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        var engine = new SyncEngine(
            pair, config, drive.Object, stateStore.Object, conflictResolver.Object,
            deletionCoordinator.Object, globFilter.Object, changeQueue, safetyValidator,
            atomicWriter, NullLogger<SyncEngine>.Instance);

        deletionCoordinator.Setup(d => d.RenotifyFrozenAsync(pair.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-1");
        drive.Setup(d => d.GetFileAsync(pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Google.Apis.Drive.v3.Data.File { Id = pair.RemoteFolderId, Name = "root" });
        drive.Setup(d => d.ListSubfoldersAsync(pair.RemoteFolderId, "", 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));

        stateStore.Setup(s => s.GetPageTokenAsync(pair.Id)).ReturnsAsync((string?)null);
        stateStore.Setup(s => s.SavePageTokenAsync(pair.Id, "token-1")).Returns(Task.CompletedTask);
        stateStore.Setup(s => s.CountRecordsAsync(pair.Id, SyncSide.Local)).ReturnsAsync(1);
        stateStore.Setup(s => s.StreamRecordsAsync(pair.Id, SyncSide.Local, It.IsAny<CancellationToken>()))
            .Returns(StreamRecords(TestHelpers.CreateFileRecord(pairId: pair.Id, relativePath: "old.txt")));

        globFilter.Setup(g => g.IsIncluded(It.IsAny<string>(), pair)).Returns(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.StartAsync(pair, cts.Token);

        var statusAfterFailure = engine.GetStatus();
        statusAfterFailure.State.Should().Be(SyncState.Error);
        statusAfterFailure.PendingChanges.Should().Be(0);

        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "later.txt"), "world");
        await Task.Delay(900);

        engine.GetStatus().PendingChanges.Should().Be(0);

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_InitialSyncWithSanitizedRemotePath_DownloadsFileAndCompletes()
    {
        using var tempDir = new TempDirectory();

        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = tempDir.Path;

        var config = TestHelpers.CreateTestSyncConfig();
        var drive = new Mock<IGoogleDriveService>();
        var stateStore = await TestHelpers.CreateInMemoryStateStoreAsync();
        var conflictResolver = new Mock<IConflictResolver>();
        var deletionCoordinator = new Mock<IDeletionCoordinator>();
        var globFilter = new Mock<IGlobFilter>();
        var changeQueue = new ChangeQueue();

        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safetyValidator = new DeletionSafetyValidator(
            config, stateStore, journal, NullLogger<DeletionSafetyValidator>.Instance);
        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        var engine = new SyncEngine(
            pair, config, drive.Object, stateStore, conflictResolver.Object,
            deletionCoordinator.Object, globFilter.Object, changeQueue, safetyValidator,
            atomicWriter, NullLogger<SyncEngine>.Instance);

        const string remoteName = "CONF: test/plan?.txt";
        var sanitizedRelativePath = PathHelper.CombineRemoteRelativePath("", remoteName);
        var remoteFile = new Google.Apis.Drive.v3.Data.File
        {
            Id = "drive-file-1",
            Name = remoteName,
            MimeType = "text/plain",
            ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow,
        };

        deletionCoordinator.Setup(d => d.RenotifyFrozenAsync(pair.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-1");
        drive.Setup(d => d.GetFileAsync(pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Google.Apis.Drive.v3.Data.File { Id = pair.RemoteFolderId, Name = "root" });
        drive.Setup(d => d.ListFilesWithPathsAsync(pair.RemoteFolderId, "", It.IsAny<CancellationToken>()))
            .Returns(ListFilesWithPaths((remoteFile, sanitizedRelativePath)));
        drive.Setup(d => d.DownloadFileAsync(remoteFile.Id, It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .Returns< string, Stream, IProgress<long>?, CancellationToken >(async (_, stream, progress, _) =>
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("hello from drive");
                await stream.WriteAsync(bytes);
                progress?.Report(bytes.Length);
            });

        globFilter.Setup(g => g.IsIncluded(It.IsAny<string>(), pair)).Returns(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.StartAsync(pair, cts.Token);

        var expectedLocalPath = Path.Combine(tempDir.Path, sanitizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(expectedLocalPath).Should().BeTrue();

        var localRecord = await stateStore.GetRecordAsync(pair.Id, SyncSide.Local, sanitizedRelativePath);
        var remoteRecord = await stateStore.GetRecordAsync(pair.Id, SyncSide.Remote, sanitizedRelativePath);

        localRecord.Should().NotBeNull();
        remoteRecord.Should().NotBeNull();
        engine.GetStatus().State.Should().NotBe(SyncState.Error);

        await engine.DisposeAsync();
        stateStore.Dispose();
        journal.Dispose();
    }

    private static async IAsyncEnumerable<FileRecord> StreamRecords(params FileRecord[] records)
    {
        foreach (var record in records)
        {
            yield return record;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<(Google.Apis.Drive.v3.Data.File File, string RelativePath)> ListFilesWithPaths(
        params (Google.Apis.Drive.v3.Data.File File, string RelativePath)[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "syncit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test temp dirs.
            }
        }
    }
}
