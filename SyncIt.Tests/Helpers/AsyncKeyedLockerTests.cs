using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

public class AsyncKeyedLockerTests
{
    [Fact]
    public async Task RunAsync_SameKey_SerializesCallers()
    {
        var locker = new AsyncKeyedLocker(StringComparer.OrdinalIgnoreCase);
        var concurrent = 0;
        var maxConcurrent = 0;
        var sync = new object();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => locker.RunAsync("Trips/Japan", async () =>
            {
                var nowRunning = Interlocked.Increment(ref concurrent);
                lock (sync)
                {
                    maxConcurrent = Math.Max(maxConcurrent, nowRunning);
                }

                await Task.Delay(25);
                Interlocked.Decrement(ref concurrent);
                return 0;
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DifferentKeys_AllowsParallelCallers()
    {
        var locker = new AsyncKeyedLocker(StringComparer.OrdinalIgnoreCase);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maxConcurrent = 0;
        var sync = new object();

        Task<int> Run(string key) => locker.RunAsync(key, async () =>
        {
            var nowRunning = Interlocked.Increment(ref concurrent);
            lock (sync)
            {
                maxConcurrent = Math.Max(maxConcurrent, nowRunning);
            }

            await gate.Task;
            Interlocked.Decrement(ref concurrent);
            return 0;
        });

        var first = Run("Trips/Japan");
        var second = Run("Trips/Thailand");

        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(first, second);

        maxConcurrent.Should().BeGreaterThanOrEqualTo(2);
    }
}
