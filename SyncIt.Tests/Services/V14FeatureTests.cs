using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;
using Change = Google.Apis.Drive.v3.Data.Change;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Tests.Services;

/// <summary>1.4.0: account colours/labels, schedule, backup-only, notifications, config diff, folder deletes, remote folder targets.</summary>
public sealed class V14FeatureTests
{
    private static SyncConfig TwoAccounts()
    {
        var c = new SyncConfig();
        c.Accounts.Add(new GoogleAccount { Id = "a1", Email = "me@example.com" });
        c.Accounts.Add(new GoogleAccount { Id = "a2", Email = "eve@example.com" });
        c.SyncPairs.Add(new SyncPair { Id = "p1", AccountId = "a1", LocalPath = @"C:\A" });
        c.SyncPairs.Add(new SyncPair { Id = "p2", AccountId = "a2", LocalPath = @"C:\B", RemoteFolderId = "f9", RemoteFolderName = "My Drive / SyncIt-Test", RemoteFolderPinned = true });
        return c;
    }

    // ── Account identity ────────────────────────────────────────────────
    [Fact]
    public void AccountColors_AreStableAndDistinctPerAccount()
    {
        var c = TwoAccounts();
        AccountColors.For(c, "a1").Should().Be(AccountColors.Palette[0]);
        AccountColors.For(c, "a2").Should().Be(AccountColors.Palette[1]);
        AccountColors.For(c, "missing").Should().Be("#707070");
        AccountColors.Initial("eve@example.com").Should().Be("E");
        AccountColors.Initial(null).Should().Be("?");
        AccountColors.Label(c, c.SyncPairs[1]).Should().Be("eve@example.com");
    }

    [Theory]
    [InlineData("root", "My Drive", null, "My Drive")]
    [InlineData("f9", "My Drive / SyncIt-Test", null, "My Drive / SyncIt-Test")]
    [InlineData("f9", "SyncIt-Test", null, "My Drive / SyncIt-Test")]
    [InlineData("d1", "Shared drive: Eng", "d1", "Shared drive: Eng")]
    [InlineData("d1", "Eng", "d1", "Shared drive: Eng")]
    public void RemoteLocation_IsHumanReadable(string id, string name, string? shared, string expected)
    {
        AccountColors.RemoteLocation(new SyncPair { RemoteFolderId = id, RemoteFolderName = name, SharedDriveId = shared })
            .Should().Be(expected);
    }

    [Fact]
    public void DescribePair_ShowsAccountLocationAndMode()
    {
        var c = TwoAccounts();
        c.SyncPairs[1].Mode = SyncMode.BackupOnly;
        var text = PairSettingsEditor.DescribePair(c.SyncPairs[1], c);
        text.Should().StartWith("eve@example.com · My Drive / SyncIt-Test").And.Contain("[backup]");
    }

    [Fact]
    public void TrayTooltip_ListsAccountsAndFitsWindowsLimit()
    {
        var s = new SyncStatus { PairStatuses =
        {
            new SyncPairStatus { AccountEmail = "me@example.com" },
            new SyncPairStatus { AccountEmail = "eve@example.com" },
        } };
        var tip = TrayTooltip.Build(s, "Up to date");
        tip.Should().Contain("[M]").And.Contain("[E]").And.Contain("2 accounts");
        tip.Length.Should().BeLessOrEqualTo(63);
        TrayTooltip.Build(new SyncStatus { PairStatuses = { new SyncPairStatus { AccountEmail = new string('x', 80) + "@example.com" } } }, "Error")
            .Length.Should().BeLessOrEqualTo(63);
    }

    // ── Schedule ────────────────────────────────────────────────────────
    [Theory]
    [InlineData(false, 8, 23, 3, true)]
    [InlineData(true, 8, 23, 7, false)]
    [InlineData(true, 8, 23, 8, true)]
    [InlineData(true, 8, 23, 22, true)]
    [InlineData(true, 8, 23, 23, false)]
    [InlineData(true, 22, 6, 23, true)]
    [InlineData(true, 22, 6, 5, true)]
    [InlineData(true, 22, 6, 12, false)]
    [InlineData(true, 9, 9, 3, true)]
    public void Schedule_ActiveHours(bool enabled, int start, int end, int hour, bool expected)
    {
        SyncSchedule.IsActive(enabled, start, end, new DateTime(2026, 10, 25, hour, 30, 0)).Should().Be(expected);
    }

    // ── Backup-only policy ──────────────────────────────────────────────
    [Fact]
    public void BackupOnly_NeverChangesThePc()
    {
        var p = new SyncPair { Mode = SyncMode.BackupOnly };
        SyncModePolicy.AllowsDownload(p).Should().BeFalse();
        SyncModePolicy.AllowsLocalDeleteFromRemote(p).Should().BeFalse();
        SyncModePolicy.ForceLocalWins(p).Should().BeTrue();
        SyncModePolicy.PropagatesLocalDeletes(p).Should().BeFalse("default keeps deleted files on Drive");
        p.BackupKeepDeletedOnDrive = false;
        SyncModePolicy.PropagatesLocalDeletes(p).Should().BeTrue();
        var two = new SyncPair();
        SyncModePolicy.AllowsDownload(two).Should().BeTrue();
        SyncModePolicy.PropagatesLocalDeletes(two).Should().BeTrue();
    }

    [Fact]
    public void ApplyMode_ReportsChanges()
    {
        var p = new SyncPair();
        PairSettingsEditor.ApplyMode(p, SyncMode.TwoWay, true).Should().BeFalse();
        PairSettingsEditor.ApplyMode(p, SyncMode.BackupOnly, true).Should().BeTrue();
        p.Mode.Should().Be(SyncMode.BackupOnly);
    }

    // ── Notifications ───────────────────────────────────────────────────
    [Fact]
    public void Notifier_FiltersBySetting_AndSummarizesBursts()
    {
        var conflict = new ActivityRecord { SyncPairId = "p2", Type = ActivityType.ConflictResolved, RelativePath = "a/report.docx" };
        var del = new ActivityRecord { SyncPairId = "p2", Type = ActivityType.Deleted, RelativePath = "b/x.txt" };
        var upload = new ActivityRecord { Type = ActivityType.Upload };
        ActivityNotifier.ShouldNotify(conflict, true, false).Should().BeTrue();
        ActivityNotifier.ShouldNotify(conflict, false, true).Should().BeFalse();
        ActivityNotifier.ShouldNotify(del, true, false).Should().BeFalse();
        ActivityNotifier.ShouldNotify(upload, true, true).Should().BeFalse();

        var one = ActivityNotifier.Summarize(new[] { conflict }, _ => "eve@example.com")!.Value;
        one.Title.Should().Contain("1 conflict resolved").And.Contain("eve@example.com");
        one.Text.Should().Be("report.docx");
        var many = ActivityNotifier.Summarize(new[] { del, del, del }, _ => null)!.Value;
        many.Title.Should().Contain("3 items deleted");
        many.Text.Should().Contain("and 2 more");
        ActivityNotifier.Summarize(Array.Empty<ActivityRecord>(), _ => null).Should().BeNull();
    }

    // ── Config reload diff ──────────────────────────────────────────────
    private static SyncConfig Clone(SyncConfig c) =>
        System.Text.Json.JsonSerializer.Deserialize<SyncConfig>(System.Text.Json.JsonSerializer.Serialize(c))!;

    [Fact]
    public void ConfigDiff_IdenticalConfig_IsNoop()
    {
        var c = TwoAccounts();
        ConfigDiff.Plan(c, Clone(c)).IsNoop.Should().BeTrue();
    }

    [Fact]
    public void ConfigDiff_HotSettingsOnly_RestartsNothing()
    {
        var c = TwoAccounts();
        var n = Clone(c);
        n.ActiveHoursEnabled = true; n.NotifyConflicts = false; n.CheckForUpdates = false;
        var plan = ConfigDiff.Plan(c, n);
        plan.IsNoop.Should().BeTrue();
        ConfigDiff.ApplyHotSettings(c, n);
        c.ActiveHoursEnabled.Should().BeTrue();
        c.NotifyConflicts.Should().BeFalse();
    }

    [Fact]
    public void ConfigDiff_AddingASecondPair_StartsOnlyThatPair()
    {
        var c = TwoAccounts();
        c.SyncPairs.RemoveAt(1);
        var n = Clone(TwoAccounts());
        var plan = ConfigDiff.Plan(c, n);
        plan.RestartAll.Should().BeFalse();
        plan.Start.Should().BeEquivalentTo(new[] { "p2" });
        plan.Stop.Should().BeEmpty();
    }

    [Fact]
    public void ConfigDiff_ChangingOnePair_RestartsOnlyThatPair()
    {
        var c = TwoAccounts();
        var n = Clone(c);
        n.SyncPairs[1].ExcludeGlobs.Add("*.bak");
        var plan = ConfigDiff.Plan(c, n);
        plan.RestartAll.Should().BeFalse();
        plan.Stop.Should().BeEquivalentTo(new[] { "p2" });
        plan.Start.Should().BeEquivalentTo(new[] { "p2" });
    }

    [Fact]
    public void ConfigDiff_PauseOfPairOrAccount_IsAPauseNotARestart()
    {
        var c = TwoAccounts();
        var n = Clone(c);
        n.Accounts[1].Paused = true;
        var plan = ConfigDiff.Plan(c, n);
        plan.Stop.Should().BeEmpty();
        plan.PauseChanges.Should().ContainKey("p2").WhoseValue.Should().BeTrue();
        n = Clone(c);
        n.SyncPairs[0].Paused = true;
        ConfigDiff.Plan(c, n).PauseChanges.Should().ContainKey("p1");
    }

    [Fact]
    public void ConfigDiff_GlobalEngineSettingOrFirstPairChange_RestartsAll()
    {
        var c = TwoAccounts();
        var n = Clone(c);
        n.PollIntervalSeconds = 99;
        ConfigDiff.Plan(c, n).RestartAll.Should().BeTrue();
        n = Clone(c);
        n.SyncPairs[0].LocalPath = @"C:\Other";
        ConfigDiff.Plan(c, n).RestartAll.Should().BeTrue();
    }

    // ── Remote folder target ────────────────────────────────────────────
    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("SyncIt-Test", "SyncIt-Test")]
    [InlineData("/My Drive/Work\\Docs/ ", "Work/Docs")]
    public void NormalizeRemoteFolderPath(string? input, string? expected)
    {
        AccountRegistry.NormalizeRemoteFolderPath(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeRemoteFolderPath_RejectsDotDot()
    {
        var act = () => AccountRegistry.NormalizeRemoteFolderPath("a/../b");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PinnedRemoteFolder_IsNeverMigratedToRoot()
    {
        var p = new SyncPair();
        AccountRegistry.ApplyRemoteFolder(p, "f9", "SyncIt-Test");
        p.RemoteFolderName.Should().Be("My Drive / SyncIt-Test");
        p.RemoteFolderId.Should().Be("f9");
        AccountRegistry.NeedsLegacyRootMigration(p).Should().BeFalse();
        AccountRegistry.NeedsLegacyRootMigration(new SyncPair { RemoteFolderId = "f9" }).Should().BeTrue("legacy pairs still migrate");
    }

    // ── Engine behaviour ────────────────────────────────────────────────
    private sealed record H(SyncEngine Engine, Mock<IGoogleDriveService> Drive, IFileStateStore Store, Mock<IDeletionCoordinator> Del, SyncPair Pair, SyncConfig Config);

    private static async Task<H> Engine(string root, Action<SyncPair>? setup = null)
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = root;
        setup?.Invoke(pair);
        var config = TestHelpers.CreateTestSyncConfig();
        var drive = new Mock<IGoogleDriveService>();
        var store = await TestHelpers.CreateInMemoryStateStoreAsync();
        var del = new Mock<IDeletionCoordinator>();
        del.Setup(d => d.SubmitAsync(It.IsAny<SyncPair>(), It.IsAny<IReadOnlyList<DeletionRecord>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var glob = new Mock<IGlobFilter>();
        glob.Setup(g => g.IsIncluded(It.IsAny<string>(), It.IsAny<SyncPair>())).Returns(true);
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safety = new DeletionSafetyValidator(config, store, journal, NullLogger<DeletionSafetyValidator>.Instance);
        var engine = new SyncEngine(pair, config, drive.Object, store, new Mock<IConflictResolver>().Object, del.Object,
            glob.Object, new ChangeQueue(), safety, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance),
            NullLogger<SyncEngine>.Instance);
        return new H(engine, drive, store, del, pair, config);
    }

    private static Task Invoke(object target, string method, params object?[] args) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!;

    [Fact]
    public async Task BackupOnly_RemoteChangeIsNotDownloaded()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path, p => p.Mode = SyncMode.BackupOnly);
        var file = new DriveFile { Id = "r1", Name = "new.txt", MimeType = "text/plain", Md5Checksum = "abc", Size = 3 };
        await Invoke(h.Engine, "HandleRemoteChangeAsync", file, "new.txt", h.Pair, CancellationToken.None);
        File.Exists(Path.Combine(temp.Path, "new.txt")).Should().BeFalse();
        h.Drive.Verify(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BackupOnly_RemoteDeleteNeverDeletesLocal()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path, p => p.Mode = SyncMode.BackupOnly);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "keep.txt"), "x");
        await h.Store.UpsertRecordAsync(new FileRecord { SyncPairId = h.Pair.Id, Side = SyncSide.Remote, RelativePath = "keep.txt", DriveFileId = "d1" });
        await Invoke(h.Engine, "HandleRemoteDeleteAsync", "d1", h.Pair, CancellationToken.None);
        File.Exists(Path.Combine(temp.Path, "keep.txt")).Should().BeTrue();
        h.Del.Verify(d => d.SubmitAsync(It.IsAny<SyncPair>(), It.IsAny<IReadOnlyList<DeletionRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BackupOnly_LocalDeleteKeepsDriveCopyByDefault()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path, p => p.Mode = SyncMode.BackupOnly);
        await h.Store.UpsertRecordAsync(new FileRecord { SyncPairId = h.Pair.Id, Side = SyncSide.Remote, RelativePath = "gone.txt", DriveFileId = "d1" });
        await Invoke(h.Engine, "HandleLocalDeleteAsync", new ChangeEvent { SyncPairId = h.Pair.Id, Kind = ChangeKind.Deleted, RelativePath = "gone.txt" }, h.Pair, CancellationToken.None);
        h.Del.Verify(d => d.SubmitAsync(It.IsAny<SyncPair>(), It.IsAny<IReadOnlyList<DeletionRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletedLocalFolder_QueuesDriveFolderForTrash_WhenAllContentIsKnown()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path);
        h.Drive.Setup(d => d.FindFileAsync(h.Pair.RemoteFolderId, "SyncIt-Test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "fold", Name = "SyncIt-Test", MimeType = "application/vnd.google-apps.folder" });
        h.Drive.Setup(d => d.ListFilesWithPathsAsync("fold", "", It.IsAny<CancellationToken>()))
            .Returns(() => Items((new DriveFile { Id = "s", MimeType = "application/vnd.google-apps.folder" }, "sub")));
        await Invoke(h.Engine, "HandleLocalDeleteAsync", new ChangeEvent { SyncPairId = h.Pair.Id, Kind = ChangeKind.Deleted, RelativePath = "SyncIt-Test" }, h.Pair, CancellationToken.None);
        h.Del.Verify(d => d.SubmitAsync(h.Pair, It.Is<IReadOnlyList<DeletionRecord>>(l =>
            l.Count == 1 && l[0].DriveFileId == "fold" && l[0].Side == SyncSide.Remote && l[0].TriggerReason == "FolderDeletedLocally"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletedLocalFolder_IsKeptOnDrive_WhenItHoldsFilesNeverSyncedHere()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path);
        h.Drive.Setup(d => d.FindFileAsync(h.Pair.RemoteFolderId, "Shared", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "fold", Name = "Shared", MimeType = "application/vnd.google-apps.folder" });
        h.Drive.Setup(d => d.ListFilesWithPathsAsync("fold", "", It.IsAny<CancellationToken>()))
            .Returns(() => Items((new DriveFile { Id = "u", Name = "unknown.pdf", MimeType = "application/pdf" }, "unknown.pdf")));
        await Invoke(h.Engine, "HandleLocalDeleteAsync", new ChangeEvent { SyncPairId = h.Pair.Id, Kind = ChangeKind.Deleted, RelativePath = "Shared" }, h.Pair, CancellationToken.None);
        h.Del.Verify(d => d.SubmitAsync(It.IsAny<SyncPair>(), It.IsAny<IReadOnlyList<DeletionRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletedLocalFolder_ThatStillExists_IsIgnored()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Here"));
        var h = await Engine(temp.Path);
        await Invoke(h.Engine, "HandleLocalDeleteAsync", new ChangeEvent { SyncPairId = h.Pair.Id, Kind = ChangeKind.Deleted, RelativePath = "Here" }, h.Pair, CancellationToken.None);
        h.Drive.Verify(d => d.FindFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingLocalRoot_IsReportedOnce_AndRecoversWhenItReturns()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "pair");
        Directory.CreateDirectory(root);
        var h = await Engine(root);
        h.Engine.LocalRootRecheck = TimeSpan.FromMilliseconds(50);
        Directory.Delete(root);
        h.Engine.CheckLocalRoot();
        h.Engine.CheckLocalRoot(); // second call must not re-report
        h.Engine.LocalRootMissing.Should().BeTrue();
        h.Engine.GetStatus().LastError.Should().Be("Local folder missing");

        Directory.CreateDirectory(root);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (h.Engine.LocalRootMissing && DateTime.UtcNow < until) await Task.Delay(50);
        h.Engine.LocalRootMissing.Should().BeFalse();
        h.Engine.GetStatus().LastError.Should().BeNull();
        await h.Engine.DisposeAsync();
    }

    [Fact]
    public async Task OutsideActiveHours_NoRemotePolling()
    {
        using var temp = new TempDirectory();
        var h = await Engine(temp.Path);
        h.Config.PollIntervalSeconds = 1;
        h.Config.ActiveHoursEnabled = true;
        h.Config.ActiveHoursStart = 8;
        h.Config.ActiveHoursEnd = 9;
        h.Engine.LocalNow = () => new DateTime(2026, 10, 25, 3, 0, 0);
        h.Del.Setup(d => d.RenotifyFrozenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        h.Drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("t");
        h.Drive.Setup(d => d.GetFileAsync(h.Pair.RemoteFolderId, It.IsAny<CancellationToken>())).ReturnsAsync(new DriveFile { Id = h.Pair.RemoteFolderId });
        h.Drive.Setup(d => d.ListFilesWithPathsAsync(h.Pair.RemoteFolderId, "", It.IsAny<CancellationToken>())).Returns(() => Items());
        h.Drive.Setup(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Change>() as IReadOnlyList<Change>, "t2"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await h.Engine.StartAsync(h.Pair, cts.Token);
        await Task.Delay(2500);
        h.Drive.Verify(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Engine.GetStatus().State.Should().Be(SyncState.Paused);

        h.Engine.LocalNow = () => new DateTime(2026, 10, 25, 8, 30, 0);
        h.Engine.WakeUp();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (h.Drive.Invocations.Any(i => i.Method.Name == "ListChangesAsync")) break;
            await Task.Delay(50);
        }
        h.Drive.Verify(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        await h.Engine.DisposeAsync();
    }

    private static async IAsyncEnumerable<(DriveFile File, string RelativePath)> Items(params (DriveFile, string)[] items)
    {
        foreach (var i in items) { yield return i; await Task.Yield(); }
    }
}
