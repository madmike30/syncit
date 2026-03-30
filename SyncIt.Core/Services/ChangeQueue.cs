using System.Collections.Concurrent;
using System.Threading.Channels;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Thread-safe change event queue with 500ms debouncing.
///
/// Problem it solves: FileSystemWatcher fires multiple events for a single save
/// (e.g. Write + Attribute change). Debouncing collapses these into one.
///
/// Architecture:
///   FSW/poller → Enqueue() → ConcurrentDictionary (pending, timer per path)
///                           → after 500ms quiet → Channel (ready)
///                                               → ReadAllAsync() consumer
/// </summary>
public sealed class ChangeQueue : IChangeQueue, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<ChangeEvent> _ready = Channel.CreateUnbounded<ChangeEvent>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
    private int _readyCount;

    public int PendingCount => _pending.Count;
    public int ReadyCount   => _readyCount;

    public void Enqueue(ChangeEvent ev)
    {
        var key = $"{ev.SyncPairId}:{ev.RelativePath}";

        // For renames, also remove any pending event for the old path (it's stale)
        if (ev.Kind == ChangeKind.Renamed && ev.OldRelativePath is not null)
        {
            var oldKey = $"{ev.SyncPairId}:{ev.OldRelativePath}";
            if (_pending.TryRemove(oldKey, out var stale))
                stale.Timer.Dispose();
        }

        while (true)
        {
            if (_pending.TryGetValue(key, out var existing))
            {
                // Reset the debounce timer and merge the events (under lock for thread safety)
                lock (existing)
                {
                    existing.Timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
                    existing.Event = Merge(existing.Event, ev);
                }
                return;
            }

            var entry = new PendingEntry { Event = ev };
            entry.Timer = new Timer(_ => Promote(key), null, DebounceDelay, Timeout.InfiniteTimeSpan);

            if (_pending.TryAdd(key, entry))
                return;

            // Race: another thread beat us — dispose our timer and loop (no recursion)
            entry.Timer.Dispose();
        }
    }

    public async IAsyncEnumerable<ChangeEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in _ready.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _readyCount);
            yield return ev;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void Promote(string key)
    {
        if (_pending.TryRemove(key, out var entry))
        {
            ChangeEvent ev;
            lock (entry)
            {
                entry.Timer.Dispose();
                ev = entry.Event;
            }
            if (_ready.Writer.TryWrite(ev))
                Interlocked.Increment(ref _readyCount);
        }
    }

    /// <summary>
    /// Merge two events for the same path into the most semantically correct single event.
    /// </summary>
    private static ChangeEvent Merge(ChangeEvent old, ChangeEvent @new) => @new.Kind switch
    {
        // Deleted always wins — it represents the final state
        ChangeKind.Deleted => @new,

        // Renamed supersedes older events
        ChangeKind.Renamed => @new,

        // Created + Modified = Created (don't lose the created kind)
        ChangeKind.Modified when old.Kind == ChangeKind.Created => old with { DetectedUtc = @new.DetectedUtc },

        // Everything else: keep the newer event
        _ => @new,
    };

    public void Dispose()
    {
        Clear();
        _ready.Writer.TryComplete();
    }

    public void Clear()
    {
        foreach (var entry in _pending.Values)
            entry.Timer.Dispose();
        _pending.Clear();

        while (_ready.Reader.TryRead(out _))
            Interlocked.Decrement(ref _readyCount);

        Interlocked.Exchange(ref _readyCount, 0);
    }

    private sealed class PendingEntry
    {
        public ChangeEvent Event { get; set; } = null!;
        public Timer Timer { get; set; } = null!;
    }
}
