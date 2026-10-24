using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Google.Apis.Drive.v3;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

/// <summary>1.3.0: multiple accounts, shared drives, auto-update, settings.</summary>
public sealed class V13FeatureTests
{
    private static readonly JsonSerializerOptions AppJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Multiple accounts ────────────────────────────────────────────────────

    private const string PreV13Config = """
    {
      "TokenStorePath": "%APPDATA%\\SyncIt\\token.bin",
      "PollIntervalSeconds": 30,
      "SyncPairs": [
        { "Id": "pair001", "LocalPath": "C:\\Users\\Me\\My Drive", "RemoteFolderId": "root", "RemoteFolderName": "My Drive",
          "Enabled": true, "SelectedSubfolders": [ "3D Print", "My Stuff" ] }
      ]
    }
    """;

    [Fact]
    public void Upgrade_From12Config_KeepsTokenAndPair_NoResignInNoResync()
    {
        var config = JsonSerializer.Deserialize<SyncConfig>(PreV13Config, AppJson)!;
        config.Accounts.Should().BeEmpty();

        var changed = AccountRegistry.Migrate(config, path => path == @"%APPDATA%\SyncIt\token.bin");

        changed.Should().BeTrue();
        config.Accounts.Should().ContainSingle();
        var account = config.Accounts[0];
        account.Id.Should().Be(AccountRegistry.LegacyAccountId);
        account.TokenStorePath.Should().Be(@"%APPDATA%\SyncIt\token.bin", "the existing sign-in is reused, not moved");
        var pair = config.SyncPairs.Single();
        pair.Id.Should().Be("pair001", "state.db is keyed by pair ID, so keeping it means no re-sync");
        pair.AccountId.Should().Be(AccountRegistry.LegacyAccountId);
        pair.RemoteFolderId.Should().Be("root");
        pair.SharedDriveId.Should().BeNull();
        pair.SelectedSubfolders.Should().Equal("3D Print", "My Stuff");
        config.CheckForUpdates.Should().BeTrue();

        // Saving and loading again is stable and does not migrate twice.
        var roundTrip = JsonSerializer.Deserialize<SyncConfig>(JsonSerializer.Serialize(config, AppJson), AppJson)!;
        AccountRegistry.Migrate(roundTrip, _ => true).Should().BeFalse();
        roundTrip.Accounts.Should().ContainSingle().Which.Id.Should().Be("default");
    }

    [Fact]
    public void Migrate_WithoutSavedSignIn_CreatesNoAccount()
    {
        var config = JsonSerializer.Deserialize<SyncConfig>(PreV13Config, AppJson)!;
        AccountRegistry.Migrate(config, _ => false).Should().BeFalse();
        config.Accounts.Should().BeEmpty();
        config.SyncPairs[0].AccountId.Should().BeNull();
    }

    [Fact]
    public void AddingAccounts_GivesEachItsOwnTokenFile_AndIgnoresDuplicates()
    {
        var config = new SyncConfig();
        AccountRegistry.Migrate(config, _ => true);
        var work = AccountRegistry.Add(config, "me@work.com");
        var again = AccountRegistry.Add(config, "ME@work.com");

        again.Should().BeSameAs(work);
        config.Accounts.Should().HaveCount(2);
        work.TokenStorePath.Should().Be($@"%APPDATA%\SyncIt\accounts\{work.Id}.bin");
        work.TokenStorePath.Should().NotBe(config.TokenStorePath);
    }

    [Fact]
    public void PairsResolveToTheirOwnAccount_UnknownFallsBackToFirst()
    {
        var config = new SyncConfig
        {
            Accounts = { new GoogleAccount { Id = "a" }, new GoogleAccount { Id = "b" } },
            SyncPairs = { new SyncPair { Id = "p1", AccountId = "b" }, new SyncPair { Id = "p2", AccountId = "gone" } },
        };
        AccountRegistry.ForPair(config, config.SyncPairs[0])!.Id.Should().Be("b");
        AccountRegistry.ForPair(config, config.SyncPairs[1])!.Id.Should().Be("a");
        AccountRegistry.Migrate(config, _ => true).Should().BeTrue();
        config.SyncPairs[1].AccountId.Should().Be("a");
    }

    [Fact]
    public void DisconnectOneAccount_RemovesOnlyItsPairs()
    {
        var config = new SyncConfig
        {
            Accounts = { new GoogleAccount { Id = "home" }, new GoogleAccount { Id = "work" } },
            SyncPairs = { new SyncPair { Id = "p1", AccountId = "home" }, new SyncPair { Id = "p2", AccountId = "work" }, new SyncPair { Id = "p3", AccountId = "work" } },
        };
        var removed = AccountRegistry.Remove(config, "work");
        removed.Select(p => p.Id).Should().Equal("p2", "p3");
        config.Accounts.Select(a => a.Id).Should().Equal("home");
        config.SyncPairs.Select(p => p.Id).Should().Equal("p1");
    }

    [Fact]
    public void FullDisconnect_AlsoDeletesEveryAccountToken()
    {
        using var temp = new TempDirectory();
        string P(string n) => Path.Combine(temp.Path, n);
        var config = new SyncConfig
        {
            TokenStorePath = P("token.bin"), StateDbPath = P("s.db"), DeleteJournalPath = P("j.db"), ActivityLogPath = P("a.db"),
            Accounts = { new GoogleAccount { Id = "default", TokenStorePath = P("token.bin") }, new GoogleAccount { Id = "acc-2", TokenStorePath = P("acc-2.bin") } },
        };
        File.WriteAllText(P("token.bin"), "x");
        File.WriteAllText(P("acc-2.bin"), "x");
        var configPath = P("syncit.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, AppJson));

        AccountReset.RemoveLocalAccount(configPath, config);

        File.Exists(P("acc-2.bin")).Should().BeFalse();
        config.Accounts.Should().BeEmpty();
        JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(configPath), AppJson)!.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void ChangingAPairsAccountOrDrive_IsDetected_SoOnlyThatPairsStateIsReset()
    {
        var old = new SyncConfig
        {
            Accounts = { new GoogleAccount { Id = "a" }, new GoogleAccount { Id = "b" } },
            SyncPairs = { new SyncPair { Id = "p1", AccountId = "a" }, new SyncPair { Id = "p2", AccountId = "a" }, new SyncPair { Id = "p3", AccountId = "a" } },
        };
        var updated = JsonSerializer.Deserialize<SyncConfig>(JsonSerializer.Serialize(old, AppJson), AppJson)!;
        updated.SyncPairs[1].AccountId = "b";
        AccountRegistry.ApplyLocation(updated.SyncPairs[2], "a", new SharedDriveInfo("0ABCdrive", "Team"));
        updated.SyncPairs[0].ExcludeGlobs.Add("*.bak");

        AccountRegistry.PairsWithChangedRemote(old, updated).Should().Equal("p2", "p3");
    }

    [Fact]
    public async Task DeletePair_ForgetsOnlyThatPair()
    {
        var store = await TestHelpers.CreateInMemoryStateStoreAsync();
        await store.UpsertRecordAsync(TestHelpers.CreateFileRecord(pairId: "p1", relativePath: "a.txt"));
        await store.UpsertRecordAsync(TestHelpers.CreateFileRecord(pairId: "p2", relativePath: "b.txt"));
        await store.SavePageTokenAsync("p1", "t1");

        await store.DeletePairAsync("p1");

        (await store.CountRecordsAsync("p1", SyncSide.Local)).Should().Be(0);
        (await store.GetPageTokenAsync("p1")).Should().BeNull();
        (await store.CountRecordsAsync("p2", SyncSide.Local)).Should().Be(1);
    }

    // ── Shared drives ────────────────────────────────────────────────────────

    [Fact]
    public void SharedDrivePair_PointsAtTheSharedDriveRoot()
    {
        var pair = new SyncPair { SelectedSubfolders = { "Old" } };
        AccountRegistry.ApplyLocation(pair, "work", new SharedDriveInfo("0AF1x", "Engineering"));
        pair.AccountId.Should().Be("work");
        pair.SharedDriveId.Should().Be("0AF1x");
        pair.RemoteFolderId.Should().Be("0AF1x");
        pair.RemoteFolderName.Should().Be("Shared drive: Engineering");
        pair.SelectedSubfolders.Should().BeEmpty();
        AccountRegistry.NeedsLegacyRootMigration(pair).Should().BeFalse("shared-drive pairs must not be moved to My Drive");

        AccountRegistry.ApplyLocation(pair, "work", null);
        pair.RemoteFolderId.Should().Be("root");
        pair.SharedDriveId.Should().BeNull();
        AccountRegistry.NeedsLegacyRootMigration(new SyncPair { RemoteFolderId = "1abc" }).Should().BeTrue();
    }

    [Fact]
    public void DriveRequests_AreScopedToTheSharedDrive()
    {
        using var drive = new DriveService();

        var list = drive.Files.List();
        DriveRequestScope.Apply(list, "0AF1x");
        list.SupportsAllDrives.Should().BeTrue();
        list.IncludeItemsFromAllDrives.Should().BeTrue();
        list.Corpora.Should().Be("drive");
        list.DriveId.Should().Be("0AF1x");

        var changes = drive.Changes.List("token");
        DriveRequestScope.Apply(changes, "0AF1x");
        changes.DriveId.Should().Be("0AF1x");
        changes.IncludeItemsFromAllDrives.Should().BeTrue();
        changes.SupportsAllDrives.Should().BeTrue();

        var start = drive.Changes.GetStartPageToken();
        DriveRequestScope.Apply(start, "0AF1x");
        start.DriveId.Should().Be("0AF1x");

        var get = drive.Files.Get("id");
        DriveRequestScope.Apply(get, null);
        get.SupportsAllDrives.Should().BeTrue("writes and reads must work on shared-drive files");
    }

    [Fact]
    public void MyDriveRequests_StayInMyDrive()
    {
        using var drive = new DriveService();
        var list = drive.Files.List();
        DriveRequestScope.Apply(list, null);
        list.IncludeItemsFromAllDrives.Should().BeFalse();
        list.DriveId.Should().BeNull();
        list.Corpora.Should().Be("user");

        var changes = drive.Changes.List("token");
        DriveRequestScope.Apply(changes, null);
        changes.DriveId.Should().BeNull();
        changes.IncludeItemsFromAllDrives.Should().BeFalse();
    }

    // ── Auto-update ──────────────────────────────────────────────────────────

    private static string Release(string tag, bool draft = false, bool prerelease = false, bool withSums = true, string host = "github.com") =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = $"https://github.com/madmike30/syncit/releases/tag/{tag}",
            assets = new[]
            {
                new { name = $"SyncIt-{tag.TrimStart('v')}-setup.exe", browser_download_url = $"https://{host}/madmike30/syncit/releases/download/{tag}/SyncIt-{tag.TrimStart('v')}-setup.exe" },
                new { name = $"SyncIt-{tag.TrimStart('v')}-portable-win-x64.zip", browser_download_url = $"https://{host}/x.zip" },
            }.Concat(withSums ? new[] { new { name = "SHA256SUMS", browser_download_url = $"https://{host}/madmike30/syncit/releases/download/{tag}/SHA256SUMS" } } : []),
        });

    [Fact]
    public void NewerRelease_IsOffered_WithSetupAndChecksums()
    {
        var update = UpdateChecker.ParseLatest(Release("v1.3.1"), new Version(1, 3, 0));
        update.Should().NotBeNull();
        update!.Version.Should().Be(new Version(1, 3, 1));
        update.SetupName.Should().Be("SyncIt-1.3.1-setup.exe");
        update.Sha256SumsUrl.Should().EndWith("/SHA256SUMS");
    }

    [Theory]
    [InlineData("v1.3.0")]
    [InlineData("v1.2.0")]
    public void SameOrOlderRelease_IsNotOffered(string tag) =>
        UpdateChecker.ParseLatest(Release(tag), new Version(1, 3, 0)).Should().BeNull();

    [Fact]
    public void DraftsPrereleasesUnverifiableOrForeignDownloads_AreNeverOffered()
    {
        var current = new Version(1, 3, 0);
        UpdateChecker.ParseLatest(Release("v2.0.0", draft: true), current).Should().BeNull();
        UpdateChecker.ParseLatest(Release("v2.0.0", prerelease: true), current).Should().BeNull();
        UpdateChecker.ParseLatest(Release("v2.0.0", withSums: false), current).Should().BeNull();
        UpdateChecker.ParseLatest(Release("v2.0.0", host: "evil.example.com"), current).Should().BeNull();
    }

    [Theory]
    [InlineData("v1.3.0", 1, 3, 0)]
    [InlineData("1.10.2", 1, 10, 2)]
    [InlineData("v2.0", 2, 0, 0)]
    [InlineData("v1.4.0-beta.1", 1, 4, 0)]
    public void Tags_AreParsed(string tag, int major, int minor, int patch)
    {
        UpdateChecker.TryParseTag(tag, out var v).Should().BeTrue();
        v.Should().Be(new Version(major, minor, patch));
    }

    [Fact]
    public void Sha256Sums_FindsTheRightLine()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  SyncIt-1.3.1-portable-win-x64.zip\n{b} *SyncIt-1.3.1-setup.exe\n";
        UpdateChecker.FindHash(sums, "SyncIt-1.3.1-setup.exe").Should().Be(new string('b', 64));
        UpdateChecker.FindHash(sums, "other.exe").Should().BeNull();
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static (UpdateInfo Update, Dictionary<string, byte[]> Files) FakeRelease(byte[] setup, string? advertisedHash = null)
    {
        var update = UpdateChecker.ParseLatest(Release("v1.3.1"), new Version(1, 3, 0))!;
        var hash = advertisedHash ?? Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();
        var files = new Dictionary<string, byte[]>
        {
            [update.SetupUrl] = setup,
            [update.Sha256SumsUrl] = Encoding.UTF8.GetBytes($"{hash}  {update.SetupName}\n"),
            ["https://api.github.com/repos/madmike30/syncit/releases/latest"] = Encoding.UTF8.GetBytes(Release("v1.3.1")),
        };
        return (update, files);
    }

    [Fact]
    public async Task Download_WithMatchingHash_IsKept()
    {
        using var temp = new TempDirectory();
        var (update, files) = FakeRelease(Encoding.UTF8.GetBytes("installer bytes"));
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(files)), NullLogger.Instance);

        (await checker.CheckAsync(new Version(1, 3, 0)))!.Version.Should().Be(new Version(1, 3, 1));
        var path = await checker.DownloadVerifiedAsync(update, temp.Path);

        File.ReadAllText(path).Should().Be("installer bytes");
    }

    [Fact]
    public async Task Download_WithWrongHash_IsRejectedAndDeleted()
    {
        using var temp = new TempDirectory();
        var (update, files) = FakeRelease(Encoding.UTF8.GetBytes("tampered"), advertisedHash: new string('0', 64));
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(files)), NullLogger.Instance);

        var act = () => checker.DownloadVerifiedAsync(update, temp.Path);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(temp.Path, update.SetupName)).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateCheck_WhenGitHubUnavailable_ReturnsNothing()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(new())), NullLogger.Instance);
        (await checker.CheckAsync(new Version(1, 3, 0))).Should().BeNull();
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void FolderSettings_AreApplied()
    {
        var pair = new SyncPair();
        PairSettingsEditor.Apply(pair, ConflictResolution.KeepBoth, "*.bak\r\n node_modules/** \n\n*.BAK, *.log", paused: true).Should().BeTrue();
        pair.ConflictPolicy.Resolution.Should().Be(ConflictResolution.KeepBoth);
        pair.ExcludeGlobs.Should().Equal("*.bak", "node_modules/**", "*.log");
        pair.Paused.Should().BeTrue();
        PairSettingsEditor.Apply(pair, ConflictResolution.KeepBoth, PairSettingsEditor.FormatPatterns(pair.ExcludeGlobs), true).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 16)]
    [InlineData(512, 512)]
    [InlineData(9_999_999, 1_048_576)]
    public void Bandwidth_IsClamped(int input, int expected) => PairSettingsEditor.ClampBandwidth(input).Should().Be(expected);

    [Fact]
    public void PairDescription_ShowsAccountAndState()
    {
        var config = new SyncConfig { Accounts = { new GoogleAccount { Id = "a", Email = "me@x.com" } } };
        var pair = new SyncPair { LocalPath = @"C:\Work", RemoteFolderName = "Shared drive: Team", AccountId = "a", Paused = true };
        PairSettingsEditor.DescribePair(pair, config).Should().Be(@"C:\Work  ↔  Shared drive: Team · me@x.com (paused)");
    }
}
