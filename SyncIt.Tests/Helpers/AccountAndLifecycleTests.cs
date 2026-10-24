using System.Text.Json;
using FluentAssertions;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Tests.Helpers;

public sealed class AccountAndLifecycleTests
{
    [Fact]
    public void EnabledUnique_DropsDuplicatePairIdsAndFolders()
    {
        var pairs = new List<SyncPair>
        {
            new() { Id = "pair001", LocalPath = @"C:\Users\You\My Drive", Enabled = true },
            new() { Id = "pair001", LocalPath = @"C:\Users\You\My Drive", Enabled = true },
            new() { Id = "pair002", LocalPath = @"c:\users\you\my drive\", Enabled = true },
            new() { Id = "pair003", LocalPath = @"D:\Other", Enabled = false },
            new() { Id = "pair004", LocalPath = @"D:\Photos", Enabled = true },
        };

        SyncPairPlanner.EnabledUnique(pairs).Select(p => p.Id).Should().Equal("pair001", "pair004");
    }

    [Fact]
    public void NextPairId_SkipsUsedIds()
    {
        var pairs = new[] { new SyncPair { Id = "pair001" }, new SyncPair { Id = "pair003" } };
        SyncPairPlanner.NextPairId(pairs).Should().Be("pair002");
    }

    [Fact]
    public async Task LifecycleGate_NeverRunsTwoOperationsAtOnce()
    {
        var gate = new EngineLifecycleGate();
        int running = 0, maxRunning = 0;

        async Task Op()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        }

        // Account switch and config reload firing together (the 1.1.0 duplicate-engine bug).
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => gate.RunAsync(Op))));
        maxRunning.Should().Be(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public void RemoveLocalAccount_DeletesTokenStateAndPairs_KeepsSettingsAndUserFiles()
    {
        using var temp = new TempDirectory();
        string P(string name) => Path.Combine(temp.Path, name);
        var syncFolder = Directory.CreateDirectory(P("My Drive")).FullName;
        File.WriteAllText(Path.Combine(syncFolder, "keep.txt"), "user data");

        var config = new SyncConfig
        {
            TokenStorePath = P("token.bin"),
            StateDbPath = P("state.db"),
            DeleteJournalPath = P("delete-journal.db"),
            ActivityLogPath = P("activity.db"),
            PollIntervalSeconds = 45,
            SyncPairs = { new SyncPair { Id = "pair001", LocalPath = syncFolder }, new SyncPair { Id = "pair002", LocalPath = syncFolder } },
        };
        foreach (var f in new[] { "token.bin", "state.db", "state.db-wal", "state.db-shm", "delete-journal.db", "activity.db" })
            File.WriteAllText(P(f), "x");
        var configPath = P("syncit.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));

        var removed = AccountReset.RemoveLocalAccount(configPath, config);

        removed.Should().HaveCount(6);
        File.Exists(P("token.bin")).Should().BeFalse();
        File.Exists(P("state.db-wal")).Should().BeFalse();
        File.Exists(Path.Combine(syncFolder, "keep.txt")).Should().BeTrue("synced files are never touched");
        config.SyncPairs.Should().BeEmpty();

        var saved = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(configPath))!;
        saved.SyncPairs.Should().BeEmpty();
        saved.PollIntervalSeconds.Should().Be(45);
    }

    [Fact]
    public void RemoveLocalAccount_WhenNothingExists_DoesNotThrow()
    {
        using var temp = new TempDirectory();
        var config = new SyncConfig { TokenStorePath = Path.Combine(temp.Path, "none.bin"),
            StateDbPath = Path.Combine(temp.Path, "s.db"), DeleteJournalPath = Path.Combine(temp.Path, "j.db"),
            ActivityLogPath = Path.Combine(temp.Path, "a.db") };
        AccountReset.RemoveLocalAccount(Path.Combine(temp.Path, "missing.json"), config).Should().BeEmpty();
    }
}
