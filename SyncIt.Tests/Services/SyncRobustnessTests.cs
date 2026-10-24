using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Google;
using Google.Apis.Requests;
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

/// <summary>Tests for the 1.2.0 sync-engine hardening (review recommendations 1-8).</summary>
public sealed class SyncRobustnessTests
{
    private static string Md5(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async IAsyncEnumerable<(DriveFile File, string RelativePath)> Items(params (DriveFile, string)[] items)
    {
        foreach (var item in items) { yield return item; await Task.Yield(); }
    }

    private sealed class Harness
    {
        public required SyncEngine Engine;
        public required Mock<IGoogleDriveService> Drive;
        public required IFileStateStore Store;
        public required Mock<IConflictResolver> Resolver;
        public required ChangeQueue Queue;
        public required SyncPair Pair;
    }

    private static async Task<Harness> CreateAsync(string localRoot, Func<IFileStateStore, Task>? seed = null, params (DriveFile, string)[] remote)
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = localRoot;
        var config = TestHelpers.CreateTestSyncConfig();
        config.PollIntervalSeconds = 3600; // no background polling during tests
        var drive = new Mock<IGoogleDriveService>();
        var store = await TestHelpers.CreateInMemoryStateStoreAsync();
        if (seed is not null) await seed(store);
        var resolver = new Mock<IConflictResolver>();
        var deletions = new Mock<IDeletionCoordinator>();
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safety = new DeletionSafetyValidator(config, store, journal, NullLogger<DeletionSafetyValidator>.Instance);
        var queue = new ChangeQueue();

        deletions.Setup(d => d.RenotifyFrozenAsync(pair.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token-1");
        drive.Setup(d => d.GetFileAsync(pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = pair.RemoteFolderId, Name = "root" });
        drive.Setup(d => d.ListFilesWithPathsAsync(pair.RemoteFolderId, "", It.IsAny<CancellationToken>()))
            .Returns(() => Items(remote));
        drive.Setup(d => d.EnsureFolderPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("folder-id");

        var engine = new SyncEngine(pair, config, drive.Object, store, resolver.Object, deletions.Object,
            new GlobFilter(), queue, safety, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance),
            NullLogger<SyncEngine>.Instance) { SettleTime = TimeSpan.Zero };
        return new Harness { Engine = engine, Drive = drive, Store = store, Resolver = resolver, Queue = queue, Pair = pair };
    }

    private static async Task SeedSyncedFileAsync(IFileStateStore store, string pairId, string root, string rel, string driveId, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        var fi = new FileInfo(abs);
        await store.UpsertRecordAsync(new FileRecord { SyncPairId = pairId, Side = SyncSide.Local, RelativePath = rel,
            SizeBytes = fi.Length, ModifiedUtc = fi.LastWriteTimeUtc, ContentHash = Md5(content) });
        await store.UpsertRecordAsync(new FileRecord { SyncPairId = pairId, Side = SyncSide.Remote, RelativePath = rel,
            DriveFileId = driveId, DriveRevisionId = "rev-1", SizeBytes = fi.Length, ModifiedUtc = fi.LastWriteTimeUtc, ContentHash = Md5(content) });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
    }

    private static void VerifyNoUploads(Mock<IGoogleDriveService> drive)
    {
        drive.Verify(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
        drive.Verify(d => d.UpdateFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 1. Duplicate same-path files on Drive ────────────────────────────────

    [Fact]
    public async Task DuplicateNamesOnDrive_AreReported_AndOnlyTheNewestIsDownloaded()
    {
        using var temp = new TempDirectory();
        var older = new DriveFile { Id = "old", Name = "a.txt", MimeType = "text/plain", Md5Checksum = Md5("old"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = new DriveFile { Id = "new", Name = "a.txt", MimeType = "text/plain", Md5Checksum = Md5("new"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow };
        var h = await CreateAsync(temp.Path, null, (newer, "a.txt"), (older, "a.txt"));
        h.Drive.Setup(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, IProgress<long>?, CancellationToken>(async (_, s, _, _) => await s.WriteAsync(Encoding.UTF8.GetBytes("new")));

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.DisposeAsync();

        h.Engine.DuplicateRemotePaths.Should().ContainSingle().Which.Should().Be("a.txt");
        h.Drive.Verify(d => d.DownloadFileAsync("new", It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Drive.Verify(d => d.DownloadFileAsync("old", It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 2. Lookup before create ──────────────────────────────────────────────

    [Fact]
    public async Task LocalOnlyFile_AlreadyOnDriveWithSameContent_IsAdoptedNotUploaded()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "report.pdf"), "pdf");
        var h = await CreateAsync(temp.Path);
        h.Drive.Setup(d => d.FindFileAsync(h.Pair.RemoteFolderId, "report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "existing", Name = "report.pdf", Md5Checksum = Md5("pdf"), Size = 3, ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow });

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.DisposeAsync();

        VerifyNoUploads(h.Drive);
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "report.pdf"))!.DriveFileId.Should().Be("existing");
    }

    [Fact]
    public async Task LocalOnlyFile_AlreadyOnDriveWithDifferentContent_UsesConflictRules_UpdatesInPlace()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "report.pdf");
        await File.WriteAllTextAsync(path, "mine");
        var h = await CreateAsync(temp.Path);
        h.Drive.Setup(d => d.FindFileAsync(h.Pair.RemoteFolderId, "report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "existing", Name = "report.pdf", MimeType = "application/pdf", Md5Checksum = Md5("theirs"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-1) });
        h.Resolver.Setup(r => r.Resolve(It.IsAny<ConflictPolicyOptions>(), It.IsAny<ConflictContext>()))
            .Returns(new ConflictResolutionAction { Outcome = ConflictOutcome.UseLocal });
        h.Drive.Setup(d => d.UpdateFileAsync("existing", path, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "existing", Md5Checksum = Md5("mine") });

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.DisposeAsync();

        h.Resolver.Verify(r => r.Resolve(It.IsAny<ConflictPolicyOptions>(), It.IsAny<ConflictContext>()), Times.Once);
        h.Drive.Verify(d => d.UpdateFileAsync("existing", path, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Drive.Verify(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 3. Locked / temporary files ──────────────────────────────────────────

    [Theory]
    [InlineData("~$Report.docx", true)]
    [InlineData(".~lock.budget.ods#", true)]
    [InlineData("video.mp4.crdownload", true)]
    [InlineData("setup.exe.part", true)]
    [InlineData("~WRL1234.tmp", true)]
    [InlineData("Report.docx", false)]
    [InlineData("partner.pdf", false)]
    public void TemporaryFiles_AreNeverSynced(string name, bool temporary)
    {
        GlobFilter.IsTemporaryFileName(name).Should().Be(temporary);
        var pair = new SyncPair { Id = "p" };
        new GlobFilter().IsIncluded("Docs/" + name, pair).Should().Be(!temporary);
    }

    [Fact]
    public void RecentlyWrittenFile_IsNotSettled_UntilQuietPeriodPasses()
    {
        using var temp = new TempDirectory();
        var f = Path.Combine(temp.Path, "x.docx");
        File.WriteAllText(f, "x");
        var written = File.GetLastWriteTimeUtc(f);
        FileLockHelper.IsSettled(f, TimeSpan.FromSeconds(2), written.AddSeconds(1)).Should().BeFalse();
        FileLockHelper.IsSettled(f, TimeSpan.FromSeconds(2), written.AddSeconds(3)).Should().BeTrue();
    }

    [Fact]
    public void LockedFile_IsNotSettled()
    {
        using var temp = new TempDirectory();
        var f = Path.Combine(temp.Path, "locked.xlsx");
        File.WriteAllText(f, "x");
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            FileLockHelper.IsSettled(f, TimeSpan.Zero, DateTime.UtcNow.AddMinutes(1)).Should().BeFalse();
        FileLockHelper.IsSettled(f, TimeSpan.Zero, DateTime.UtcNow.AddMinutes(1)).Should().BeTrue();
    }

    // ── 4. Renames, including case-only ──────────────────────────────────────

    [Fact]
    public async Task LocalCaseOnlyRename_RenamesDriveFileInPlace_NoReupload()
    {
        using var temp = new TempDirectory();
        var oldPath = Path.Combine(temp.Path, "notes.txt");
        await File.WriteAllTextAsync(oldPath, "hello");
        var h = await CreateAsync(temp.Path, s => SeedSyncedFileAsync(s, "test0001", temp.Path, "notes.txt", "drv-1", "hello"));
        h.Drive.Setup(d => d.MoveFileAsync("drv-1", "Notes.txt", h.Pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "drv-1", Name = "Notes.txt", Md5Checksum = Md5("hello"), HeadRevisionId = "rev-1", ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow });

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        File.Move(oldPath, Path.Combine(temp.Path, "tmp.txt"));
        File.Move(Path.Combine(temp.Path, "tmp.txt"), Path.Combine(temp.Path, "Notes.txt"));
        h.Queue.Enqueue(new ChangeEvent { SyncPairId = h.Pair.Id, Side = SyncSide.Local, Kind = ChangeKind.Renamed,
            RelativePath = "Notes.txt", OldRelativePath = "notes.txt" });

        await WaitUntilAsync(() => h.Drive.Invocations.Any(i => i.Method.Name == nameof(IGoogleDriveService.MoveFileAsync)));
        await Task.Delay(300);
        await h.Engine.DisposeAsync();

        h.Drive.Verify(d => d.MoveFileAsync("drv-1", "Notes.txt", h.Pair.RemoteFolderId, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoUploads(h.Drive);
        h.Drive.Verify(d => d.TrashFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "Notes.txt"))!.DriveFileId.Should().Be("drv-1");
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "notes.txt")).Should().BeNull();
    }

    [Fact]
    public async Task RemoteCaseOnlyRename_RenamesLocalFile_WithoutDownloading()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes.txt"), "hello");
        var h = await CreateAsync(temp.Path, s => SeedSyncedFileAsync(s, "test0001", temp.Path, "notes.txt", "drv-1", "hello"));
        var renamed = new DriveFile { Id = "drv-1", Name = "Notes.txt", MimeType = "text/plain", Md5Checksum = Md5("hello"), HeadRevisionId = "rev-1", ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow };
        h.Drive.Setup(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Change>)new[] { new Change { FileId = "drv-1", File = renamed } }, "token-2"));
        h.Drive.Setup(d => d.GetRelativePathAsync("drv-1", h.Pair.RemoteFolderId, It.IsAny<CancellationToken>())).ReturnsAsync("Notes.txt");

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.TriggerSyncAsync(CancellationToken.None);
        await h.Engine.DisposeAsync();

        Directory.GetFiles(temp.Path).Select(Path.GetFileName).Should().Contain("Notes.txt").And.NotContain("notes.txt");
        h.Drive.Verify(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "Notes.txt")).Should().NotBeNull();
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "notes.txt")).Should().BeNull();
    }

    [Fact]
    public void CaseOnlyChange_IsDetected()
    {
        PathKey.IsCaseOnlyChange("a/notes.txt", "a/Notes.txt").Should().BeTrue();
        PathKey.IsCaseOnlyChange("a/notes.txt", "a/notes.txt").Should().BeFalse();
        PathKey.IsCaseOnlyChange("a/notes.txt", "b/notes.txt").Should().BeFalse();
    }

    // ── 5. Long paths ────────────────────────────────────────────────────────

    [Fact]
    public void LongPaths_GetExtendedPrefix_ForNativeCalls()
    {
        var shortPath = @"C:\Users\You\My Drive\a.txt";
        PathHelper.ToExtendedPath(shortPath).Should().Be(shortPath);
        var longPath = @"C:\Users\You\My Drive\" + string.Join('\\', Enumerable.Repeat(new string('x', 50), 6)) + @"\file.txt";
        PathHelper.ToExtendedPath(longPath).Should().Be(@"\\?\" + longPath);
        var longUnc = @"\\server\share\" + string.Join('\\', Enumerable.Repeat(new string('y', 50), 6)) + @"\f.txt";
        PathHelper.ToExtendedPath(longUnc).Should().Be(@"\\?\UNC\server\share\" + longUnc[15..]);
        PathHelper.ToExtendedPath(@"\\?\" + longPath).Should().Be(@"\\?\" + longPath);
    }

    [Fact]
    public async Task FileInDeepFolder_BeyondMaxPath_IsAdoptedOnFirstSync()
    {
        using var temp = new TempDirectory();
        var rel = string.Join('/', Enumerable.Repeat(new string('d', 60), 4)) + "/deep.txt";
        var abs = Path.Combine(temp.Path, rel.Replace('/', '\\'));
        abs.Length.Should().BeGreaterThan(260);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllTextAsync(abs, "deep");
        var h = await CreateAsync(temp.Path, null,
            (new DriveFile { Id = "deep", Name = "deep.txt", MimeType = "text/plain", Md5Checksum = Md5("deep"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow }, rel));

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.DisposeAsync();

        VerifyNoUploads(h.Drive);
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, rel))!.DriveFileId.Should().Be("deep");
    }

    // ── 6. Expired change token ──────────────────────────────────────────────

    [Fact]
    public async Task ExpiredChangeToken_TakesFreshToken_AndRescansWithoutDeleting()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "kept.txt"), "k");
        var h = await CreateAsync(temp.Path, s => SeedSyncedFileAsync(s, "test0001", temp.Path, "kept.txt", "drv-k", "k"),
            (new DriveFile { Id = "drv-k", Name = "kept.txt", MimeType = "text/plain", Md5Checksum = Md5("k"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow }, "kept.txt"));
        h.Drive.Setup(d => d.ListChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GoogleApiException("drive", "Page token expired") { HttpStatusCode = HttpStatusCode.Gone });
        h.Drive.SetupSequence(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-1").ReturnsAsync("fresh-token");

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.TriggerSyncAsync(CancellationToken.None);
        await h.Engine.DisposeAsync();

        (await h.Store.GetPageTokenAsync(h.Pair.Id)).Should().Be("fresh-token");
        h.Drive.Verify(d => d.ListFilesWithPathsAsync(h.Pair.RemoteFolderId, "", It.IsAny<CancellationToken>()), Times.Once);
        h.Drive.Verify(d => d.TrashFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoUploads(h.Drive);
        h.Engine.GetStatus().State.Should().NotBe(SyncState.Error);
    }

    [Fact]
    public void ExpiredPageToken_IsRecognized()
    {
        DriveErrors.IsExpiredPageToken(new GoogleApiException("drive", "gone") { HttpStatusCode = HttpStatusCode.Gone }).Should().BeTrue();
        DriveErrors.IsExpiredPageToken(new GoogleApiException("drive", "bad")
        {
            HttpStatusCode = HttpStatusCode.BadRequest,
            Error = new RequestError { Errors = new List<SingleError> { new() { Reason = "invalid", Location = "pageToken" } } },
        }).Should().BeTrue();
        DriveErrors.IsExpiredPageToken(new GoogleApiException("drive", "other") { HttpStatusCode = HttpStatusCode.BadRequest }).Should().BeFalse();
    }

    // ── 7. Unicode normalization ─────────────────────────────────────────────

    [Fact]
    public void PathKey_TreatsComposedAndDecomposedNamesAsEqual()
    {
        var composed = "Caf\u00e9/R\u00e9sum\u00e9.pdf";
        var decomposed = "Cafe\u0301/Re\u0301sume\u0301.pdf";
        PathKey.Comparer.Equals(composed, decomposed).Should().BeTrue();
        PathKey.Comparer.GetHashCode(composed).Should().Be(PathKey.Comparer.GetHashCode(decomposed));
        PathKey.Comparer.Equals("\u05d3\u05d9\u05e8\u05d4/a.txt", "\u05d3\u05d9\u05e8\u05d4/A.TXT").Should().BeTrue(); // Hebrew
    }

    [Fact]
    public async Task DecomposedLocalName_MatchesComposedDriveName_NoTransfer()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Re\u0301sume\u0301.pdf"), "cv");
        var h = await CreateAsync(temp.Path, null,
            (new DriveFile { Id = "cv", Name = "R\u00e9sum\u00e9.pdf", MimeType = "application/pdf", Md5Checksum = Md5("cv"), ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow }, "R\u00e9sum\u00e9.pdf"));

        await h.Engine.StartAsync(h.Pair, CancellationToken.None);
        await h.Engine.DisposeAsync();

        VerifyNoUploads(h.Drive);
        h.Drive.Verify(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 8. Rate limits ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, true)]
    [InlineData(HttpStatusCode.BadGateway, null, true)]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded", true)]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", true)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", false)]
    [InlineData(HttpStatusCode.NotFound, null, false)]
    public void DriveErrors_RetryOnlyTransientAndRateLimit(HttpStatusCode status, string? reason, bool transient)
    {
        var ex = new GoogleApiException("drive", "x") { HttpStatusCode = status };
        if (reason is not null) ex.Error = new RequestError { Errors = new List<SingleError> { new() { Reason = reason } } };
        DriveErrors.IsTransient(ex).Should().Be(transient);
    }

    [Fact]
    public async Task DrivePolicy_RetriesA403RateLimit_ThenSucceeds()
    {
        var policy = RetryHelper.CreateDrivePolicy(NullLogger.Instance, new RetryPolicyConfig { RetryCount = 3, BaseDelayMs = 1, MaxDelayMs = 5 });
        var calls = 0;
        var result = await policy.ExecuteAsync(() =>
        {
            if (++calls < 3)
                throw new GoogleApiException("drive", "slow down")
                {
                    HttpStatusCode = HttpStatusCode.Forbidden,
                    Error = new RequestError { Errors = new List<SingleError> { new() { Reason = "userRateLimitExceeded" } } },
                };
            return Task.FromResult("ok");
        });
        result.Should().Be("ok");
        calls.Should().Be(3);
    }
}
