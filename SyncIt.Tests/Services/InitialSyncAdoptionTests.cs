using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Tests.Services;

/// <summary>
/// First sync (or sync after a reset) with files already on both sides:
/// items must be matched and adopted, never copied again or duplicated.
/// </summary>
public sealed class InitialSyncAdoptionTests
{
    private static string Md5(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async IAsyncEnumerable<(DriveFile File, string RelativePath)> Items(params (DriveFile, string)[] items)
    {
        foreach (var item in items) { yield return item; await Task.Yield(); }
    }

    private sealed record Harness(SyncEngine Engine, Mock<IGoogleDriveService> Drive, IFileStateStore Store,
        Mock<IConflictResolver> Resolver, Mock<IDeletionCoordinator> Deletions, SyncPair Pair);

    private static async Task<Harness> CreateAsync(string localRoot, params (DriveFile, string)[] remote)
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.LocalPath = localRoot;
        var config = TestHelpers.CreateTestSyncConfig();
        var drive = new Mock<IGoogleDriveService>();
        var store = await TestHelpers.CreateInMemoryStateStoreAsync();
        var resolver = new Mock<IConflictResolver>();
        var deletions = new Mock<IDeletionCoordinator>();
        var glob = new Mock<IGlobFilter>();
        var journal = await TestHelpers.CreateInMemoryDeletionJournalAsync();
        var safety = new DeletionSafetyValidator(config, store, journal, NullLogger<DeletionSafetyValidator>.Instance);

        deletions.Setup(d => d.RenotifyFrozenAsync(pair.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        drive.Setup(d => d.GetStartPageTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token-1");
        drive.Setup(d => d.GetFileAsync(pair.RemoteFolderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = pair.RemoteFolderId, Name = "root" });
        drive.Setup(d => d.ListFilesWithPathsAsync(pair.RemoteFolderId, "", It.IsAny<CancellationToken>()))
            .Returns(() => Items(remote));
        drive.Setup(d => d.EnsureFolderPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("folder-id");
        glob.Setup(g => g.IsIncluded(It.IsAny<string>(), pair)).Returns(true);

        var engine = new SyncEngine(pair, config, drive.Object, store, resolver.Object, deletions.Object,
            glob.Object, new ChangeQueue(), safety, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance),
            NullLogger<SyncEngine>.Instance);
        return new Harness(engine, drive, store, resolver, deletions, pair);
    }

    private static void VerifyNoTransfers(Mock<IGoogleDriveService> drive)
    {
        drive.Verify(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
        drive.Verify(d => d.UpdateFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
        drive.Verify(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        drive.Verify(d => d.TrashFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IdenticalFolderTreeOnBothSides_IsAdoptedWithZeroTransfers()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Trips", "Thailand"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Trips", "Thailand", "plan.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes.txt"), "hello");

        var h = await CreateAsync(temp.Path,
            (new DriveFile { Id = "f-trips", Name = "Trips", MimeType = "application/vnd.google-apps.folder" }, "Trips"),
            (new DriveFile { Id = "f-thai", Name = "Thailand", MimeType = "application/vnd.google-apps.folder" }, "Trips/Thailand"),
            (new DriveFile { Id = "plan", Name = "plan.txt", MimeType = "text/plain", Md5Checksum = Md5("same"), Size = 4,
                ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow }, "Trips/Thailand/plan.txt"),
            (new DriveFile { Id = "notes", Name = "notes.txt", MimeType = "text/plain", Md5Checksum = Md5("hello"), Size = 5,
                ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow }, "notes.txt"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.StartAsync(h.Pair, cts.Token);
        await h.Engine.DisposeAsync();

        VerifyNoTransfers(h.Drive);
        h.Drive.Verify(d => d.EnsureFolderPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "folders that already exist on Drive must not be created again");
        h.Resolver.Verify(r => r.Resolve(It.IsAny<ConflictPolicyOptions>(), It.IsAny<ConflictContext>()), Times.Never);
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Local, "Trips/Thailand/plan.txt")).Should().NotBeNull();
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "notes.txt"))!.DriveFileId.Should().Be("notes");
    }

    [Fact]
    public async Task ExistingFileWithSameContent_IsAdoptedIntoBaseline()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Skill.docx"), "content");

        var h = await CreateAsync(temp.Path,
            (new DriveFile { Id = "d1", Name = "Skill.docx", MimeType = "application/octet-stream", Md5Checksum = Md5("content"),
                Size = 7, ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-3) }, "Skill.docx"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.StartAsync(h.Pair, cts.Token);
        await h.Engine.DisposeAsync();

        VerifyNoTransfers(h.Drive);
        var local = await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Local, "Skill.docx");
        var remote = await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "Skill.docx");
        local!.ContentHash.Should().Be(Md5("content"));
        remote!.DriveFileId.Should().Be("d1");
    }

    [Fact]
    public async Task SamePathDifferentContent_LocalWins_UpdatesDriveFileInPlace_NoDuplicate()
    {
        using var temp = new TempDirectory();
        var localFile = Path.Combine(temp.Path, "Handle.3mf");
        await File.WriteAllTextAsync(localFile, "local version");

        var remoteFile = new DriveFile { Id = "drive-handle", Name = "Handle.3mf", MimeType = "application/octet-stream",
            Md5Checksum = Md5("old remote"), Size = 10, ModifiedTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-1) };
        var h = await CreateAsync(temp.Path, (remoteFile, "Handle.3mf"));

        h.Resolver.Setup(r => r.Resolve(It.IsAny<ConflictPolicyOptions>(), It.IsAny<ConflictContext>()))
            .Returns(new ConflictResolutionAction { Outcome = ConflictOutcome.UseLocal });
        h.Drive.Setup(d => d.UpdateFileAsync("drive-handle", localFile, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "drive-handle", Md5Checksum = Md5("local version"), Size = 13 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.StartAsync(h.Pair, cts.Token);
        await h.Engine.DisposeAsync();

        h.Drive.Verify(d => d.UpdateFileAsync("drive-handle", localFile, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Drive.Verify(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never, "a second 'Handle.3mf' must not be created on Drive");
        (await h.Store.GetRecordAsync(h.Pair.Id, SyncSide.Remote, "Handle.3mf"))!.DriveFileId.Should().Be("drive-handle");
    }

    [Fact]
    public async Task FirstSync_NeverDeletesAnything()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "local-only.txt"), "x");
        var h = await CreateAsync(temp.Path);
        h.Drive.Setup(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveFile { Id = "new", Md5Checksum = Md5("x"), Size = 1 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.StartAsync(h.Pair, cts.Token);
        await h.Engine.DisposeAsync();

        File.Exists(Path.Combine(temp.Path, "local-only.txt")).Should().BeTrue();
        h.Drive.Verify(d => d.TrashFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
