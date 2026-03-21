using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Helpers;

internal static class TestHelpers
{
    private static int _counter;

    public static async Task<FileStateStore> CreateInMemoryStateStoreAsync()
    {
        var name = $"statestore_{Interlocked.Increment(ref _counter)}";
        var connStr = $"Data Source={name};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();

        var store = new FileStateStore(name, keepAlive, NullLogger<FileStateStore>.Instance);
        await store.InitializeAsync();
        return store;
    }

    public static async Task<DeletionJournal> CreateInMemoryDeletionJournalAsync()
    {
        var name = $"journal_{Interlocked.Increment(ref _counter)}";
        var connStr = $"Data Source={name};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();

        var journal = new DeletionJournal(name, keepAlive, NullLogger<DeletionJournal>.Instance);
        await journal.InitializeAsync();
        return journal;
    }

    public static SyncPair CreateTestSyncPair(string? id = null) => new()
    {
        Id = id ?? "test0001",
        LocalPath = Path.Combine(Path.GetTempPath(), "syncit-test-" + Guid.NewGuid().ToString("N")[..8]),
        RemoteFolderId = "remote-folder-id-123",
        RemoteFolderName = "TestDrive",
        Enabled = true,
        ExcludeGlobs = ["*.tmp", "~$*"],
        IncludeGlobs = [],
        SelectedSubfolders = [],
    };

    public static SyncConfig CreateTestSyncConfig() => new()
    {
        PollIntervalSeconds = 5,
        MaxDeletionsPerCycle = 10,
        DeletionGraceCycles = 2,
        TokenStorePath = Path.Combine(Path.GetTempPath(), "syncit-test-token.bin"),
        StateDbPath = ":memory:",
        DeleteJournalPath = ":memory:",
        LogDirectory = Path.Combine(Path.GetTempPath(), "syncit-test-logs"),
        LocalTrashPath = Path.Combine(Path.GetTempPath(), "syncit-test-trash"),
    };

    public static FileRecord CreateFileRecord(
        string pairId = "test0001",
        SyncSide side = SyncSide.Local,
        string relativePath = "docs/file.txt",
        string? contentHash = "abc123",
        DateTime? modifiedUtc = null) => new()
    {
        SyncPairId = pairId,
        Side = side,
        RelativePath = relativePath,
        SizeBytes = 1024,
        ModifiedUtc = modifiedUtc ?? DateTime.UtcNow,
        ContentHash = contentHash,
        RecordedUtc = DateTime.UtcNow,
        MimeType = "text/plain",
    };

    public static DeletionRecord CreateDeletionRecord(
        string pairId = "test0001",
        SyncSide side = SyncSide.Local,
        string relativePath = "docs/file.txt",
        int graceCycles = 2,
        DeletionStatus status = DeletionStatus.Pending) => new()
    {
        SyncPairId = pairId,
        Side = side,
        RelativePath = relativePath,
        SizeBytes = 1024,
        ModifiedUtc = DateTime.UtcNow,
        TriggerReason = "DeletedLocally",
        Status = status,
        GraceCyclesRemaining = graceCycles,
    };
}
