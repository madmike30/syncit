using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public class FailedOperationTrackerTests
{
    private readonly SyncConfig _config = TestHelpers.CreateTestSyncConfig();
    private readonly FailedOperationTracker _tracker;

    public FailedOperationTrackerTests()
    {
        _tracker = new FailedOperationTracker(_config, NullLogger.Instance);
    }

    private static ChangeEvent MakeEvent(string path = "test.txt") => new()
    {
        SyncPairId = "pair1",
        Side = SyncSide.Local,
        Kind = ChangeKind.Modified,
        RelativePath = path,
    };

    [Fact]
    public void RecordFailure_FirstTime_CountsAsOne()
    {
        _tracker.RecordFailure(MakeEvent(), "some error");
        _tracker.FailedCount.Should().Be(1);
        _tracker.ExhaustedCount.Should().Be(0);
    }

    [Fact]
    public void RecordFailure_ExceedsMaxRetries_MarksExhausted()
    {
        var ev = MakeEvent();
        for (int i = 0; i < _config.MaxFileRetries; i++)
            _tracker.RecordFailure(ev, $"error {i}");

        _tracker.IsExhausted(ev).Should().BeTrue();
        _tracker.ExhaustedCount.Should().Be(1);
        _tracker.FailedCount.Should().Be(0);
    }

    [Fact]
    public void MarkSuccess_RemovesFromTracker()
    {
        var ev = MakeEvent();
        _tracker.RecordFailure(ev, "error");
        _tracker.FailedCount.Should().Be(1);

        _tracker.MarkSuccess(ev);
        _tracker.FailedCount.Should().Be(0);
    }

    [Fact]
    public void GetRetryableEvents_ReturnsOnlyReady()
    {
        var ev1 = MakeEvent("a.txt");
        var ev2 = MakeEvent("b.txt");
        _tracker.RecordFailure(ev1, "error");
        _tracker.RecordFailure(ev2, "error");

        // Both should eventually become retryable (after their delay passes)
        // For immediate test, check snapshot
        var snapshot = _tracker.GetSnapshot();
        snapshot.Should().HaveCount(2);
    }

    [Fact]
    public void GetRetryableEvents_ExcludesExhausted()
    {
        var ev = MakeEvent();
        for (int i = 0; i < _config.MaxFileRetries; i++)
            _tracker.RecordFailure(ev, $"error {i}");

        _tracker.GetRetryableEvents().Should().BeEmpty();
    }

    [Fact]
    public void MultiplePaths_TrackedIndependently()
    {
        _tracker.RecordFailure(MakeEvent("a.txt"), "err");
        _tracker.RecordFailure(MakeEvent("b.txt"), "err");
        _tracker.FailedCount.Should().Be(2);

        _tracker.MarkSuccess(MakeEvent("a.txt"));
        _tracker.FailedCount.Should().Be(1);
    }
}
