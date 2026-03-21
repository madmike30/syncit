using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Safety;

public sealed class DeletionSafetyValidatorTests : IAsyncLifetime
{
    private FileStateStore _store = null!;
    private DeletionJournal _journal = null!;
    private DeletionSafetyValidator _validator = null!;
    private SyncConfig _config = null!;
    private SyncPair _pair = null!;

    public async Task InitializeAsync()
    {
        _store = await TestHelpers.CreateInMemoryStateStoreAsync();
        _journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        _config = TestHelpers.CreateTestSyncConfig();
        _config.MaxDeletionsPerCycle = 3;
        _pair = TestHelpers.CreateTestSyncPair();
        _validator = new DeletionSafetyValidator(
            _config, _store, _journal, NullLogger<DeletionSafetyValidator>.Instance);
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        _journal.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateAsync_EmptyDeletionList_ReturnsProceed()
    {
        var result = await _validator.ValidateAsync(_pair, Array.Empty<DeletionRecord>());

        result.ShouldFreeze.Should().BeFalse();
        result.ApprovedDeletions.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_AllDeletionsHaveBaseline_UnderThreshold_ReturnsProceed()
    {
        // Create baselines in the store
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "a.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "b.txt"));

        var deletions = new List<DeletionRecord>
        {
            TestHelpers.CreateDeletionRecord(relativePath: "a.txt"),
            TestHelpers.CreateDeletionRecord(relativePath: "b.txt"),
        };

        var result = await _validator.ValidateAsync(_pair, deletions);

        result.ShouldFreeze.Should().BeFalse();
        result.ApprovedDeletions.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValidateAsync_ExceedsThreshold_ReturnsFreeze()
    {
        // Create baselines and deletions that exceed the threshold of 3
        for (int i = 0; i < 5; i++)
        {
            var path = $"file{i}.txt";
            await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: path));
        }

        var deletions = Enumerable.Range(0, 5)
            .Select(i => TestHelpers.CreateDeletionRecord(relativePath: $"file{i}.txt"))
            .ToList();

        var result = await _validator.ValidateAsync(_pair, deletions);

        result.ShouldFreeze.Should().BeTrue();
        result.FreezeReason.Should().Contain("limit: 3");
    }

    [Fact]
    public async Task ValidateAsync_NoDeletionBaseline_RejectsDeletion()
    {
        // Don't create a baseline — the file was never tracked
        var deletions = new List<DeletionRecord>
        {
            TestHelpers.CreateDeletionRecord(relativePath: "phantom.txt"),
        };

        var result = await _validator.ValidateAsync(_pair, deletions);

        result.ShouldFreeze.Should().BeFalse();
        result.ApprovedDeletions.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_DeletedBaseline_RejectsDeletion()
    {
        // Create a baseline but mark it as deleted
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "deleted.txt"));
        await _store.MarkDeletedAsync("test0001", SyncSide.Local, "deleted.txt");

        var deletions = new List<DeletionRecord>
        {
            TestHelpers.CreateDeletionRecord(relativePath: "deleted.txt"),
        };

        var result = await _validator.ValidateAsync(_pair, deletions);

        result.ApprovedDeletions.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ExistingPendingPlusNew_ExceedsThreshold_Freezes()
    {
        // Already have 2 pending in journal
        for (int i = 0; i < 2; i++)
        {
            var path = $"existing{i}.txt";
            await _journal.RecordAsync(TestHelpers.CreateDeletionRecord(relativePath: path));
        }

        // Adding 2 more (total = 4 > threshold of 3)
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "new1.txt"));
        await _store.UpsertRecordAsync(TestHelpers.CreateFileRecord(relativePath: "new2.txt"));

        var deletions = new List<DeletionRecord>
        {
            TestHelpers.CreateDeletionRecord(relativePath: "new1.txt"),
            TestHelpers.CreateDeletionRecord(relativePath: "new2.txt"),
        };

        var result = await _validator.ValidateAsync(_pair, deletions);

        result.ShouldFreeze.Should().BeTrue();
    }

    [Fact]
    public async Task CheckLocalFolderAsync_FolderDoesNotExist_ReturnsFail()
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = @"C:\nonexistent\path\that\does\not\exist";

        var result = await _validator.CheckLocalFolderAsync(pair);

        result.IsOk.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task CheckLocalFolderAsync_NoBaseline_ReturnsOk()
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = Path.GetTempPath(); // exists

        var result = await _validator.CheckLocalFolderAsync(pair);

        result.IsOk.Should().BeTrue();
    }
}
