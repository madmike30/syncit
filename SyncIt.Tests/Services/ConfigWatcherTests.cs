using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Services;

public sealed class ConfigWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syncit-cw-" + Guid.NewGuid().ToString("N"));
    private string ConfigFile => Path.Combine(_dir, "syncit.json");

    public ConfigWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigFile, Json(30));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Json(int poll) => "{ \"PollIntervalSeconds\": " + poll + " }";

    private ConfigWatcher Create(TimeSpan? poll = null) =>
        new(ConfigFile, new SyncConfig(), NullLogger<ConfigWatcher>.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(100), pollInterval: poll ?? TimeSpan.FromMinutes(10));

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public void SameContent_IsIgnored_ChangedContent_ReloadsOnce()
    {
        using var w = Create();
        var count = 0;
        w.ConfigReloaded += _ => count++;

        w.TryReload().Should().BeFalse();          // unchanged since construction
        File.WriteAllText(ConfigFile, Json(30));   // harmless touch
        w.TryReload().Should().BeFalse();
        File.WriteAllText(ConfigFile, Json(45));
        w.TryReload().Should().BeTrue();
        w.TryReload().Should().BeFalse();          // not applied twice
        count.Should().Be(1);
    }

    [Fact]
    public async Task EditMadeBeforeStart_IsPickedUpOnStart()
    {
        using var w = Create();
        File.WriteAllText(ConfigFile, Json(60));   // e.g. while engines were still starting
        var reloaded = 0;
        w.ConfigReloaded += _ => Interlocked.Increment(ref reloaded);
        w.Start();
        (await WaitFor(() => reloaded == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task AtomicReplace_IsPickedUp()
    {
        using var w = Create();
        var reloaded = 0;
        w.ConfigReloaded += _ => Interlocked.Increment(ref reloaded);
        w.Start();
        var tmp = ConfigFile + ".tmp";
        File.WriteAllText(tmp, Json(90));
        File.Move(tmp, ConfigFile, overwrite: true);
        (await WaitFor(() => reloaded == 1)).Should().BeTrue();
        w.Current.PollIntervalSeconds.Should().Be(90);
    }

    [Fact]
    public void LockedFile_IsRetried()
    {
        using var w = Create();
        File.WriteAllText(ConfigFile, Json(75));
        var fs = new FileStream(ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(300).ContinueWith(_ => fs.Dispose());
        w.TryReload().Should().BeTrue();
    }
}
