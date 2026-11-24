using System.Collections.Concurrent;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The node's memory-only queue of markers to remove after their writer deleted the in-place copy; a lost item leaves an orphan and never a stale read.</summary>
internal sealed class UnmarkQueue(TimeProvider clock, KeyMoveOptions options, Func<KeyMoveData?> data, bool background = true) : IDisposable
{
    private readonly ConcurrentQueue<Item> _items = new();
    private readonly CancellationTokenSource _stop = new();
    private int _count;
    private int _running;
    private int _disposed;
    private int _saturated;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Above half the capacity until the queue is below a quarter: a writer then removes its marker before it acknowledges.</summary>
    public bool Saturated => Volatile.Read(ref _saturated) == 1;

    // A disposed source reads as stopped, so a caller after Dispose behaves as before the source was released.
    private CancellationToken StopToken
    {
        get
        {
            try
            {
                return _stop.Token;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(true);
            }
        }
    }

    /// <returns>False when the queue is full: the marker is dropped and counted, and stays as an orphan.</returns>
    public bool Enqueue(RedisKey control, string field, string value, string cache, string group)
    {
        if (Interlocked.Increment(ref _count) > options.UnmarkQueueMax || _stop.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _count);
            Drop(cache, group);
            return false;
        }

        UpdateSaturation();
        _items.Enqueue(new Item(control, field, value, cache, group, 0, clock.GetTimestamp()));
        if (background && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(() => LoopAsync(StopToken), CancellationToken.None);
        }

        return true;
    }

    /// <summary>One pass over what is due, one script per control record and batch; tests call it directly.</summary>
    public async Task RunDueAsync(bool all = false)
    {
        var now = clock.GetTimestamp();
        var due = new List<Item>();
        var later = new List<Item>();
        while (_items.TryDequeue(out var item))
        {
            (all || item.Due <= now ? due : later).Add(item);
        }

        foreach (var item in later)
        {
            _items.Enqueue(item);
        }

        foreach (var group in due.GroupBy(i => (string)i.Control!))
        {
            foreach (var batch in group.Chunk(options.UnmarkBatch))
            {
                await SendAsync(batch).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    private static void Drop(string cache, string group) => RouterTelemetry.CountMarker("unmark_dropped", cache, group);

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * clock.TimestampFrequency);

    private void UpdateSaturation()
    {
        var count = Count;
        if (count > options.UnmarkQueueMax / 2)
        {
            Volatile.Write(ref _saturated, 1);
        }
        else if (count < options.UnmarkQueueMax / 4)
        {
            Volatile.Write(ref _saturated, 0);
        }
    }

    private async Task SendAsync(Item[] batch)
    {
        try
        {
            if (data() is not { } plane)
            {
                throw new InvalidOperationException("No data connection.");
            }

            _ = await plane.Scripts.UnmarkManyAsync(batch[0].Control, [.. batch.Select(b => (b.Field, b.Value))]).ConfigureAwait(false);
            Interlocked.Add(ref _count, -batch.Length);
            UpdateSaturation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            foreach (var item in batch)
            {
                if (item.Attempts + 1 >= options.UnmarkAttempts)
                {
                    Interlocked.Decrement(ref _count);
                    Drop(item.Cache, item.Group);
                    UpdateSaturation();
                }
                else
                {
                    var backoff = TimeSpan.FromTicks(Math.Min(options.RetryInitial.Ticks << item.Attempts, options.RetryMax.Ticks));
                    _items.Enqueue(item with { Attempts = item.Attempts + 1, Due = clock.GetTimestamp() + Ticks(backoff) });
                }
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_items.IsEmpty)
            {
                await Task.Delay(options.UnmarkFlushInterval, clock, token).ConfigureAwait(false);
                try
                {
                    await RunDueAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: what is left stays as orphans.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (!_items.IsEmpty && !token.IsCancellationRequested && background && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
            }
        }
    }

    private sealed record Item(RedisKey Control, string Field, string Value, string Cache, string Group, int Attempts, long Due);
}
