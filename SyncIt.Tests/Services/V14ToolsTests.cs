using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncIt.Core.Crypto;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SyncIt.Tests.Services;

/// <summary>1.4.0 second list: encryption, duplicates, preview, removable drives, links, version history.</summary>
public sealed class V14ToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syncit-tools-" + Guid.NewGuid().ToString("N"));
    public V14ToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly Lazy<(VaultCrypto C, VaultCrypto.VaultParams P)> Vault = new(() =>
    {
        var p = VaultCrypto.CreateParams("correct horse battery", out var c);
        return (c, p);
    });

    private static byte[] RoundTrip(VaultCrypto c, byte[] data)
    {
        using var enc = new MemoryStream();
        c.Encrypt(new MemoryStream(data), enc);
        enc.Position = 0;
        using var dec = new MemoryStream();
        c.Decrypt(enc, dec);
        return dec.ToArray();
    }

    // ── Encryption (beta) ───────────────────────────────────────────────
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(VaultCrypto.ChunkSize - 1)] [InlineData(VaultCrypto.ChunkSize)]
    [InlineData(VaultCrypto.ChunkSize + 1)] [InlineData(3 * VaultCrypto.ChunkSize)] [InlineData(5 * VaultCrypto.ChunkSize + 777)]
    public void Vault_RoundTripsAnySize(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        RoundTrip(Vault.Value.C, data).Should().Equal(data);
    }

    [Fact]
    public void Vault_CiphertextIsRandomizedAndHidesContent()
    {
        var data = new byte[1000];
        using var a = new MemoryStream(); using var b = new MemoryStream();
        Vault.Value.C.Encrypt(new MemoryStream(data), a); Vault.Value.C.Encrypt(new MemoryStream(data), b);
        a.ToArray().Should().NotEqual(b.ToArray());
        a.ToArray().Skip(80).Take(64).Should().NotEqual(new byte[64]);
    }

    [Theory]
    [InlineData("flip")] [InlineData("truncate")] [InlineData("dropLastChunk")] [InlineData("swapChunks")] [InlineData("header")]
    public void Vault_DetectsTampering(string attack)
    {
        var c = Vault.Value.C;
        using var enc = new MemoryStream();
        c.Encrypt(new MemoryStream(RandomNumberGenerator.GetBytes(3 * VaultCrypto.ChunkSize + 10)), enc);
        var bytes = enc.ToArray();
        const int header = 76, chunk = VaultCrypto.ChunkSize + 16;
        switch (attack)
        {
            case "flip": bytes[header + 100] ^= 1; break;
            case "truncate": bytes = bytes[..^5]; break;
            case "dropLastChunk": bytes = bytes[..(header + 3 * chunk)]; break;
            case "swapChunks":
                var tmp = bytes[header..(header + chunk)];
                Array.Copy(bytes, header + chunk, bytes, header, chunk);
                Array.Copy(tmp, 0, bytes, header + chunk, chunk);
                break;
            case "header": bytes[20] ^= 1; break;
        }
        var act = () => c.Decrypt(new MemoryStream(bytes), new MemoryStream());
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Vault_WrongPassphraseIsRejected_RightOneOpens()
    {
        var p = Vault.Value.P;
        ((Action)(() => VaultCrypto.Open("wrong passphrase!", p))).Should().Throw<CryptographicException>();
        var reopened = VaultCrypto.Open("correct horse battery", p);
        reopened.DecryptName(Vault.Value.C.EncryptName("Report.docx")).Should().Be("Report.docx");
        ((Action)(() => VaultCrypto.CreateParams("short", out _))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Vault_NamesAreDeterministicSafeAndTamperProof()
    {
        var c = Vault.Value.C;
        var e1 = c.EncryptName("Tax return 2025 (final).pdf");
        e1.Should().Be(c.EncryptName("Tax return 2025 (final).pdf"));
        e1.Should().MatchRegex("^[a-z2-7]+$").And.NotContain("Tax");
        c.DecryptName(e1).Should().Be("Tax return 2025 (final).pdf");
        c.DecryptPath(c.EncryptPath(@"Photos\2024\קובץ.jpg")).Should().Be("Photos/2024/קובץ.jpg");
        var tampered = (e1[0] == 'a' ? "b" : "a") + e1[1..];
        ((Action)(() => c.DecryptName(tampered))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Base32_RoundTrips()
    {
        for (int n = 0; n < 40; n++)
        {
            var d = RandomNumberGenerator.GetBytes(n);
            Base32.Decode(Base32.Encode(d)).Should().Equal(d);
        }
    }

    [Fact]
    public async Task EncryptedBackup_NeverDeletes_UploadsOnlyChanges_AndRestoresIntoEmptyFolder()
    {
        var src = Path.Combine(_dir, "src"); Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "bravo");

        var store = new Dictionary<string, (string Name, string Parent, byte[] Data)>();
        var drive = new Mock<IGoogleDriveService>(MockBehavior.Strict);
        drive.Setup(d => d.EnsureFolderPathAsync("root", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string _, string p, CancellationToken _) => "F:" + p);
        drive.Setup(d => d.FindFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DriveFile?)null);
        drive.Setup(d => d.UploadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "application/octet-stream", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, string name, string parent, string _, CancellationToken _) =>
            {
                var id = "id" + store.Count; store[id] = (name, parent, File.ReadAllBytes(path)); return new DriveFile { Id = id, Name = name };
            });
        drive.Setup(d => d.UpdateFileAsync(It.IsAny<string>(), It.IsAny<string>(), "application/octet-stream", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string path, string _, CancellationToken _) => { store[id] = store[id] with { Data = File.ReadAllBytes(path) }; return new DriveFile { Id = id }; });

        var runner = new EncryptedBackupRunner(drive.Object, Vault.Value.C, Path.Combine(_dir, "m.json"), NullLogger.Instance);
        (await runner.BackupAsync(src, "root", _ => true)).Uploaded.Should().Be(2);
        (await runner.BackupAsync(src, "root", _ => true)).Uploaded.Should().Be(0);
        store.Values.Should().OnlyContain(v => !v.Name.Contains("txt"));
        store.Values.SelectMany(v => v.Data).Should().NotBeEmpty();

        File.Delete(Path.Combine(src, "a.txt"));                       // local delete: nothing happens on Drive
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "bravo 2");
        File.SetLastWriteTimeUtc(Path.Combine(src, "sub", "b.txt"), DateTime.UtcNow.AddMinutes(1));
        (await runner.BackupAsync(src, "root", _ => true)).Uploaded.Should().Be(1);
        store.Should().HaveCount(2);
        drive.Verify(d => d.TrashFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Restore
        var c = Vault.Value.C;
        var tree = store.Select(kv => (new DriveFile { Id = kv.Key, Name = kv.Value.Name, MimeType = "application/octet-stream" },
            kv.Value.Parent == "root" ? kv.Value.Name : kv.Value.Parent[2..] + "/" + kv.Value.Name)).ToList();
        drive.Setup(d => d.ListTreeAsync("root", It.IsAny<CancellationToken>())).ReturnsAsync(tree);
        drive.Setup(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string id, Stream s, CancellationToken _) => s.WriteAsync(store[id].Data).AsTask());
        var notEmpty = Path.Combine(_dir, "busy"); Directory.CreateDirectory(notEmpty); File.WriteAllText(Path.Combine(notEmpty, "x"), "x");
        await FluentActions.Awaiting(() => runner.RestoreAsync("root", notEmpty)).Should().ThrowAsync<IOException>();
        var target = Path.Combine(_dir, "restore");
        (await runner.RestoreAsync("root", target)).Should().Be(2);
        File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be("alpha");
        File.ReadAllText(Path.Combine(target, "sub", "b.txt")).Should().Be("bravo 2");
        Directory.Exists(src).Should().BeTrue();
    }

    // ── Duplicate finder ────────────────────────────────────────────────
    private static (DriveFile, string) F(string id, string rel, string md5, int day, long size = 10) =>
        (new DriveFile { Id = id, Name = rel.Split('/')[^1], Md5Checksum = md5, Size = size, MimeType = "application/pdf", CreatedTimeDateTimeOffset = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) }, rel);

    [Fact]
    public void Duplicates_SameFolderNameAndMd5Only_OldestOrLinkedKept()
    {
        var files = new[]
        {
            F("a1", "Docs/r.pdf", "AA", 3), F("a2", "Docs/r.pdf", "aa", 1), F("a3", "Docs/r.pdf", "aa", 5),
            F("b1", "Other/r.pdf", "aa", 1),                  // other folder → not a duplicate
            F("c1", "Docs/s.pdf", "aa", 1),                   // other name
            F("d1", "Docs/r2.pdf", "bb", 1), F("d2", "Docs/r2.pdf", "cc", 2), // different content
            F("e1", "x.bin", "ee", 1), F("e2", "x.bin", "ee", 2),
        };
        var groups = DuplicateFinder.Find(files, new HashSet<string> { "e2" });
        groups.Should().HaveCount(2);
        var g = groups.Single(x => x.Name == "r.pdf");
        g.Copies.Single(c => c.Protected).DriveFileId.Should().Be("a2");
        g.Copies.Where(c => c.Suggested).Select(c => c.DriveFileId).Should().BeEquivalentTo("a1", "a3");
        var e = groups.Single(x => x.Name == "x.bin");
        e.Copies.Single(c => c.Protected).DriveFileId.Should().Be("e2");

        DuplicateFinder.SafeToTrash(groups, new HashSet<string> { "a1", "a2", "a3", "e2" }).Select(c => c.DriveFileId)
            .Should().BeEquivalentTo("a1", "a3");
    }

    // ── Dry-run preview ─────────────────────────────────────────────────
    [Fact]
    public async Task Preview_CountsUploadsDownloadsMatchesConflicts_NeverDeletes()
    {
        var pair = new SyncPair { Id = "p", LocalPath = @"C:\X" };
        var local = new[] { new LocalEntry(@"same.txt", 3), new LocalEntry(@"diff.txt", 3), new LocalEntry(@"new\up.txt", 4), new LocalEntry("big.iso", long.MaxValue) };
        var remote = new[]
        {
            (new DriveFile { Name = "same.txt", Size = 3, Md5Checksum = "m1", MimeType = "text/plain" }, "same.txt"),
            (new DriveFile { Name = "diff.txt", Size = 3, Md5Checksum = "zz", MimeType = "text/plain" }, "diff.txt"),
            (new DriveFile { Name = "down.txt", Size = 9, Md5Checksum = "m3", MimeType = "text/plain" }, "down.txt"),
            (new DriveFile { Name = "Doc", MimeType = GoogleDocExportMap.DocMime }, "Doc"),
            (new DriveFile { Name = "f", MimeType = "application/vnd.google-apps.folder" }, "f"),
        };
        var r = await SyncPreview.BuildAsync(pair, local, remote, _ => true, _ => Task.FromResult("m1"));
        r.Count(PreviewAction.Upload).Should().Be(1);
        r.Count(PreviewAction.Download).Should().Be(2);
        r.Items.Should().Contain(i => i.RelativePath == "Doc.docx");
        r.Count(PreviewAction.Match).Should().Be(1);
        r.Count(PreviewAction.Conflict).Should().Be(1);
        r.Count(PreviewAction.Skip).Should().Be(1);
        r.Deletes.Should().Be(0);

        pair.Mode = SyncMode.BackupOnly;
        var b = await SyncPreview.BuildAsync(pair, local, remote, _ => true, _ => Task.FromResult("m1"));
        b.Count(PreviewAction.Download).Should().Be(0);
        b.Count(PreviewAction.KeepOnDrive).Should().Be(2);
    }

    // ── Removable drives ────────────────────────────────────────────────
    private sealed class FakeVolumes : IVolumeInfo
    {
        public Dictionary<string, string?> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetSerial(string root) => Map.TryGetValue(root, out var s) ? s : null;
    }

    [Fact]
    public void RemovableDrives_BindOnlyOffSystemDisk_AndRequireSameDisk()
    {
        var v = new FakeVolumes(); v.Map[@"E:\"] = "1234ABCD";
        var usb = new SyncPair { LocalPath = @"E:\Photos" };
        RemovableDrives.Bind(usb, v, @"C:\");
        usb.VolumeSerial.Should().Be("1234ABCD");
        RemovableDrives.IsAvailable(usb, v).Should().BeTrue();
        v.Map[@"E:\"] = null;                    RemovableDrives.IsAvailable(usb, v).Should().BeFalse(); // unplugged
        v.Map[@"E:\"] = "99999999";              RemovableDrives.IsAvailable(usb, v).Should().BeFalse(); // another stick on E:
        var sys = new SyncPair { LocalPath = @"C:\Users\x\Docs" };
        RemovableDrives.Bind(sys, v, @"C:\");
        sys.VolumeSerial.Should().BeNull();
        RemovableDrives.IsAvailable(sys, v).Should().BeTrue();
        RemovableDrives.ShouldBind(@"\\nas\share\x", @"C:\").Should().BeFalse();
    }

    // ── Drive links ─────────────────────────────────────────────────────
    [Fact]
    public async Task DriveLinks_ResolveByTypeAndLocatePair()
    {
        DriveLinks.For("x", GoogleDocExportMap.SheetMime).Should().Be("https://docs.google.com/spreadsheets/d/x/edit");
        DriveLinks.For("x", "application/pdf").Should().Be("https://drive.google.com/file/d/x/view");
        var cfg = new SyncConfig();
        cfg.SyncPairs.Add(new SyncPair { Id = "a", LocalPath = _dir, Enabled = true });
        cfg.SyncPairs.Add(new SyncPair { Id = "b", LocalPath = Path.Combine(_dir, "inner"), Enabled = true });
        DriveLinks.Locate(cfg, Path.Combine(_dir, "inner", "f.txt"))!.Value.Pair.Id.Should().Be("b");
        DriveLinks.Locate(cfg, Path.Combine(_dir, "q", "f.txt"))!.Value.RelativePath.Should().Be("q/f.txt");
        DriveLinks.Locate(cfg, Path.GetTempPath() + "elsewhere.txt").Should().BeNull();

        var store = new Mock<IFileStateStore>();
        store.Setup(s => s.GetRecordAsync("a", SyncSide.Remote, "q/f.txt")).ReturnsAsync(new FileRecord { DriveFileId = "ID1", MimeType = "text/plain" });
        (await DriveLinks.ResolveAsync(cfg, Path.Combine(_dir, "q", "f.txt"), store.Object, _ => null)).Should().Be("https://drive.google.com/file/d/ID1/view");
        (await DriveLinks.ResolveAsync(cfg, Path.Combine(_dir, "q", "none.txt"), store.Object, _ => null)).Should().BeNull();
    }

    // ── Version history ─────────────────────────────────────────────────
    [Fact]
    public async Task VersionRestore_KeepsCurrentCopyInTrash_AndVerifiesChecksum()
    {
        var root = Path.Combine(_dir, "pair"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "n.txt"); File.WriteAllText(file, "current");
        var pair = new SyncPair { Id = "p", LocalPath = root };
        var store = new Mock<IFileStateStore>();
        store.Setup(s => s.GetRecordAsync("p", SyncSide.Remote, "n.txt")).ReturnsAsync(new FileRecord { DriveFileId = "D", MimeType = "text/plain" });
        var old = "old content"u8.ToArray();
        var drive = new Mock<IGoogleDriveService>();
        drive.Setup(d => d.DownloadRevisionAsync("D", "r1", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, Stream s, CancellationToken _) => s.WriteAsync(old).AsTask());
        var trash = new LocalTrashManager(Path.Combine(_dir, "trash"), NullLogger<LocalTrashManager>.Instance);
        var svc = new VersionHistoryService(drive.Object, store.Object, trash, NullLogger.Instance);

        var bad = new DriveRevisionInfo("r1", DateTime.UtcNow, old.Length, "00000000000000000000000000000000", false);
        await FluentActions.Awaiting(() => svc.RestoreRevisionAsync(pair, "n.txt", bad)).Should().ThrowAsync<IOException>();
        File.ReadAllText(file).Should().Be("current");

        var good = bad with { Md5 = Convert.ToHexString(MD5.HashData(old)).ToLowerInvariant() };
        var saved = await svc.RestoreRevisionAsync(pair, "n.txt", good);
        File.ReadAllText(file).Should().Be("old content");
        saved.Should().NotBeNull();
        File.ReadAllText(saved!).Should().Be("current");
        Directory.GetFiles(root).Should().ContainSingle();
    }
}
