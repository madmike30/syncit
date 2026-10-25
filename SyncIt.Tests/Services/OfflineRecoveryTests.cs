using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly.Timeout;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;
using Change = Google.Apis.Drive.v3.Data.Change;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Tests.Services;

/// <summary>After a network outage the sync loop must keep retrying, show Offline, and recover on its own.</summary>
public sealed class OfflineRecoveryTests
{
    private static HttpRequestException NoSuchHost() =>
        new("No such host is known. (www.googleapis.com:443)", new SocketException(11001));

    [Fact]
    public void NetworkErrors_DetectsDnsSocketAndTimeouts()
    {
        NetworkErrors.IsNetworkError(NoSuchHost()).Should().BeTrue();
        NetworkErrors.IsNetworkError(new SocketException(10060)).Should().BeTrue();
        NetworkErrors.IsNetworkError(new TimeoutRejectedException()).Should().BeTrue();
        NetworkErrors.IsNetworkError(new TaskCanceledException("HttpClient timeout")).Should().BeTrue();
        NetworkErrors.IsNetworkError(new InvalidOperationException("x", NoSuchHost())).Should().BeTrue();
    }

    [Fact]
    public void NetworkErrors_IgnoresNonNetworkFailures()
    {
        NetworkErrors.IsNetworkError(new InvalidOperationException("bug")).Should().BeFalse();
        NetworkErrors.IsNetworkError(new HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized)).Should().BeFalse();
        NetworkErrors.IsNetworkError(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 30, 30)]
    [InlineData(1, 30, 30)]
    [InlineData(2, 30, 60)]
    [InlineData(3, 30, 120)]
    [InlineData(5, 30, 300)]
    [InlineData(50, 30, 300)]
    [InlineData(4, 600, 600)]
    public void RecoveryBackoff_GrowsAndIsCapped(int failures, int poll, int expectedSeconds)
    {
        RecoveryBackoff.Next(failures, poll, 300).TotalSeconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public async Task DrivePolicy_AbandonsAHungAttemptAndRetries()
    {
        var policy = RetryHelper.CreateDrivePolicy(NullLogger.Instance,
            new RetryPolicyConfig { RetryCount = 2, BaseDelayMs = 1, MaxDelayMs = 5 }, TimeSpan.FromMilliseconds(200));
        var calls = 0;
        var result = await policy.ExecuteAsync(async () =>
        {
            if (++calls == 1) await Task.Delay(Timeout.Infinite); // half-open connection: never returns
            return "ok";
        });
        result.Should().Be("ok");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task PollLoop_GoesOfflineDuringOutage_ThenRecoversWithoutRestart()
    {
        using var temp = new TempDirectory();
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = temp.Path;
        var config = TestHelpers.CreateTestSyncConfig();
        config.PollIntervalSeconds = 1;
        var drive = new Mock<IGoogleDriveService>();
        var store = await TestHelpers.CreateInMemoryStateStoreAsync();
        var deletions = new Mock<IDeletionCoordinator>();
        var glob = new Mock<IGlobFilter>();
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safety = new DeletionSafetyValidator(config, store, journal, NullLogger<DeletionSafetyValidator>.Instance);

        deletions.Setup(d => d.RenotifyFrozenAsync(pair.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deletions.Setup(d => d.ExecuteApprovedAsync(It.IsAny<SyncPair>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token-1");
        drive.Setup(d => d.GetFileAsync(pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = pair.RemoteFolderId, Name = "root" });
        drive.Setup(d => d.ListFilesWithPathsAsync(pair.RemoteFolderId, "", It.IsAny<CancellationToken>()))
            .Returns(() => Empty());
        glob.Setup(g => g.IsIncluded(It.IsAny<string>(), pair)).Returns(true);

        var calls = 0;
        var outage = true;
        drive.Setup(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref calls);
                if (Volatile.Read(ref outage)) throw NoSuchHost();
                return Task.FromResult<(IReadOnlyList<Change>, string)>((new List<Change>(), "token-2"));
            });

        var engine = new SyncEngine(pair, config, drive.Object, store, new Mock<IConflictResolver>().Object, deletions.Object,
            glob.Object, new ChangeQueue(), safety, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance),
            NullLogger<SyncEngine>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(pair, cts.Token);
        try
        {
            await WaitUntil(() => Volatile.Read(ref calls) >= 2, cts.Token);
            engine.GetStatus().State.Should().Be(SyncState.Offline);
            engine.ConsecutiveCycleFailures.Should().BeGreaterThan(0);

            // Network returns: the wake-up signal must trigger a cycle right away, even during backoff.
            Volatile.Write(ref outage, false);
            var before = Volatile.Read(ref calls);
            engine.WakeUp();
            await WaitUntil(() => Volatile.Read(ref calls) > before && engine.ConsecutiveCycleFailures == 0, cts.Token);

            var status = engine.GetStatus();
            status.State.Should().Be(SyncState.Idle);
            status.LastError.Should().BeNull();

            // And it keeps polling afterwards (loop is still alive).
            var afterRecovery = Volatile.Read(ref calls);
            await WaitUntil(() => Volatile.Read(ref calls) > afterRecovery, cts.Token);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private static async IAsyncEnumerable<(DriveFile File, string RelativePath)> Empty()
    {
        await Task.Yield();
        yield break;
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(50, ct);
    }
}
