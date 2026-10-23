namespace UiPath.Caching;

public abstract partial class MultilayerCacheBase
{
    private const string TopicInvalidationEvent = "cache.batch.topic_invalidation";

    private protected int BatchChunkSize { get; private init; } = InMemoryRedisCacheOptions.DefaultBatchChunkSize;

    private protected int BatchConcurrentChunks { get; private init; } = InMemoryRedisCacheOptions.DefaultBatchConcurrentChunks;

    private protected int? BatchTopicInvalidationThreshold { get; private init; }

    /// <summary>Starts every call before awaiting any, so they share the pipeline; a throw counts as failed.</summary>
    private protected static async ValueTask<bool[]> AllAsync<TItem, TState>(TItem[] items, int count, TState state, Func<TState, TItem, ValueTask<bool>> start, Action<TState, TItem, Exception> onError) =>
        (await IssueAsync(items, count, state, start, onError, CancellationToken.None).ConfigureAwait(false)).Results;

    /// <summary>As <see cref="AllAsync"/>, but a cancel stops the calls not yet started.</summary>
    private protected static async ValueTask<Issued> IssueAsync<TItem, TState>(TItem[] items, int count, TState state, Func<TState, TItem, ValueTask<bool>> start, Action<TState, TItem, Exception> onError, CancellationToken token)
    {
        var pending = new ValueTask<bool>[count];
        var results = new bool[count];
        var started = new bool[count];
        var issued = 0;
        for (; issued < count && !token.IsCancellationRequested; issued++)
        {
            try
            {
#pragma warning disable CA2012 // started together so they share the pipeline, each awaited exactly once below
                pending[issued] = start(state, items[issued]);
#pragma warning restore CA2012
                started[issued] = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onError(state, items[issued], ex);
            }
        }

        for (var i = 0; i < issued; i++)
        {
            if (started[i])
            {
                results[i] = await ResultAsync(pending[i], state, items[i], onError, token).ConfigureAwait(false);
            }
        }

        return new Issued(results, issued, token.IsCancellationRequested);
    }

    private protected static void AddFailed(ref List<CacheKey>? failed, List<CacheKey>? more)
    {
        if (more is not null)
        {
            (failed ??= []).AddRange(more);
        }
    }

    /// <summary>Each key's last index; null when every key is unique.</summary>
    private protected static Dictionary<CacheKey, int>? LastOccurrences<TItem>(TItem[] chunk, int count, Func<TItem, CacheKey> keyOf)
    {
        if (count < 2)
        {
            return null;
        }

        var last = new Dictionary<CacheKey, int>(count);
        for (var i = 0; i < count; i++)
        {
            last[keyOf(chunk[i])] = i;
        }

        return last.Count < count ? last : null;
    }

    /// <summary>Runs <paramref name="process"/> over chunks of the input, a few in flight at a time.</summary>
    /// <remarks>A cancel throws between or within chunks and leaves what ran done; a known size above the threshold gets one topic-wide event, not one per key.</remarks>
    private protected async ValueTask<IReadOnlyList<CacheKey>> RunBatchAsync<TItem, TState>(
        IEnumerable<TItem> items,
        TState state,
        Func<TItem, CacheKey> keyOf,
        Func<TState, TItem[], int, bool, ValueTask<List<CacheKey>?>> process,
        Func<TState, CacheKey, ValueTask<bool>> publishTopicWide,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(items);
        var topicWide = BatchTopicInvalidationThreshold is { } threshold && items.TryGetNonEnumeratedCount(out var total) && total > threshold;
        var run = new BatchRun<TItem, TState>(state, process, !topicWide, BatchChunkSize, BatchConcurrentChunks, token);
        CacheKey? first = null;
        try
        {
            foreach (var item in items)
            {
                first ??= keyOf(item);
                await run.AddAsync(item).ConfigureAwait(false);
            }

            await run.FinishAsync().ConfigureAwait(false);
        }
        catch
        {
            await run.DrainAsync().ConfigureAwait(false);

            // The chunks written before the failure or the cancel have no per-key events: the one event covers them.
            if (topicWide && run.Started && first is { } issuedFirst)
            {
                await AnnounceTopicWideAsync(items, state, keyOf, publishTopicWide, issuedFirst, run.Failed).ConfigureAwait(false);
            }

            throw;
        }

        // Every write landed, so the event goes out even if the caller cancels now.
        if (topicWide && first is { } firstKey)
        {
            return await AnnounceTopicWideAsync(items, state, keyOf, publishTopicWide, firstKey, run.Failed).ConfigureAwait(false);
        }

        return run.Failed;
    }

    private static async ValueTask<bool> ResultAsync<TItem, TState>(ValueTask<bool> pending, TState state, TItem item, Action<TState, TItem, Exception> onError, CancellationToken token)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            onError(state, item, ex);
            return false;
        }
    }

    private async ValueTask<IReadOnlyList<CacheKey>> AnnounceTopicWideAsync<TItem, TState>(IEnumerable<TItem> items, TState state, Func<TItem, CacheKey> keyOf, Func<TState, CacheKey, ValueTask<bool>> publishTopicWide, CacheKey first, List<CacheKey> failed)
    {
        var announced = false;
        try
        {
            announced = await publishTopicWide(state, first).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The topic-wide invalidation of {Cache} could not be published.", Name);
        }

        Telemetry.TryTrackEvent(TopicInvalidationEvent, [new("cache.name", Name), new("announced", announced ? "true" : "false")]);

        // Without its event no write here has been announced: every key goes back to the caller, and a repeat is harmless.
        return announced ? failed : items.Select(keyOf).ToList();
    }

    /// <summary>The first <see cref="Count"/> items were issued, the rest not.</summary>
    private protected readonly record struct Issued(bool[] Results, int Count, bool Cancelled);

    private sealed class BatchRun<TItem, TState>(TState state, Func<TState, TItem[], int, bool, ValueTask<List<CacheKey>?>> process, bool announce, int chunkSize, int concurrent, CancellationToken token)
    {
        private readonly Queue<Task<List<CacheKey>?>> _running = new();
        private TItem[]? _chunk;
        private int _count;

        public List<CacheKey> Failed { get; } = [];

        /// <summary>Whether any chunk may have written.</summary>
        public bool Started { get; private set; }

        public async ValueTask AddAsync(TItem item)
        {
            _chunk ??= new TItem[chunkSize];
            _chunk[_count++] = item;
            if (_count == _chunk.Length)
            {
                await StartAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask FinishAsync()
        {
            if (_count > 0)
            {
                await StartAsync().ConfigureAwait(false);
            }

            while (_running.TryDequeue(out var next))
            {
                Collect(await next.ConfigureAwait(false));
            }
        }

        public async ValueTask DrainAsync()
        {
            while (_running.TryDequeue(out var next))
            {
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch
                {
                    // Awaited only so it is not left running unobserved; the first failure is the one rethrown.
                }
            }
        }

        private async ValueTask StartAsync()
        {
            token.ThrowIfCancellationRequested();
            var taken = _chunk!;
            var takenCount = _count;
            _chunk = null;
            _count = 0;
            Started = true;
            _running.Enqueue(process(state, taken, takenCount, announce).AsTask());
            while (_running.Count >= concurrent && _running.TryDequeue(out var next))
            {
                Collect(await next.ConfigureAwait(false));
            }
        }

        private void Collect(List<CacheKey>? chunkFailed)
        {
            if (chunkFailed is not null)
            {
                Failed.AddRange(chunkFailed);
            }
        }
    }
}
