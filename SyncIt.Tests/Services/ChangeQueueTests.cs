using FluentAssertions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Services;

public sealed class ChangeQueueTests : IDisposable
{
    private readonly ChangeQueue _queue = new();

    public void Dispose() => _queue.Dispose();

    private static ChangeEvent MakeEvent(
        string path = "docs/file.txt",
        ChangeKind kind = ChangeKind.Modified,
        string pairId = "pair1") => new()
    {
        SyncPairId = pairId,
        RelativePath = path,
        Kind = kind,
        Side = SyncSide.Local,
    };

    [Fact]
    public async Task Enqueue_SingleEvent_ProducedAfterDebounce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ev = MakeEvent();

        _queue.Enqueue(ev);

        var produced = new List<ChangeEvent>();
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            produced.Add(item);
            break; // we only expect one
        }

        produced.Should().HaveCount(1);
        produced[0].RelativePath.Should().Be("docs/file.txt");
    }

    [Fact]
    public async Task Enqueue_MultipleEventsForSamePath_MergedIntoOne()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _queue.Enqueue(MakeEvent(kind: ChangeKind.Created));
        _queue.Enqueue(MakeEvent(kind: ChangeKind.Modified));

        // Wait for debounce to promote
        await Task.Delay(700);

        _queue.ReadyCount.Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_CreatedThenModified_MergesAsCreated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _queue.Enqueue(MakeEvent(kind: ChangeKind.Created));
        _queue.Enqueue(MakeEvent(kind: ChangeKind.Modified));

        var produced = new List<ChangeEvent>();
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            produced.Add(item);
            break;
        }

        produced[0].Kind.Should().Be(ChangeKind.Created);
    }

    [Fact]
    public async Task Enqueue_DeleteOverwritesPriorEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _queue.Enqueue(MakeEvent(kind: ChangeKind.Created));
        _queue.Enqueue(MakeEvent(kind: ChangeKind.Deleted));

        var produced = new List<ChangeEvent>();
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            produced.Add(item);
            break;
        }

        produced[0].Kind.Should().Be(ChangeKind.Deleted);
    }

    [Fact]
    public async Task Enqueue_DifferentPaths_ProduceSeparateEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _queue.Enqueue(MakeEvent(path: "a.txt"));
        _queue.Enqueue(MakeEvent(path: "b.txt"));

        // Wait for debounce
        await Task.Delay(700);

        _queue.ReadyCount.Should().Be(2);
    }

    [Fact]
    public async Task Enqueue_RapidEvents_ResetsDebounceTimer()
    {
        // Enqueue, wait 300ms (less than 500ms debounce), enqueue again
        _queue.Enqueue(MakeEvent());
        await Task.Delay(300);
        _queue.Enqueue(MakeEvent(kind: ChangeKind.Modified));

        // After 300ms from second enqueue, still pending (debounce restarted)
        await Task.Delay(300);
        _queue.ReadyCount.Should().Be(0);

        // After another 300ms, should be promoted
        await Task.Delay(400);
        _queue.ReadyCount.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_CompletesChannel()
    {
        _queue.Enqueue(MakeEvent());
        _queue.Dispose();

        // ReadAllAsync should complete when channel is closed
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in _queue.ReadAllAsync(cts.Token)) { }
        });

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().Be(readTask);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentEnqueue_NoExceptions()
    {
        const int threadCount = 10;
        const int eventsPerThread = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(t =>
            Task.Run(() =>
            {
                for (int i = 0; i < eventsPerThread; i++)
                    _queue.Enqueue(MakeEvent(path: $"thread{t}/file{i}.txt"));
            }));

        await Task.WhenAll(tasks);

        // Wait for all debounces to fire
        await Task.Delay(1000);

        // Each unique path should produce one event
        _queue.ReadyCount.Should().Be(threadCount * eventsPerThread);
    }
}
