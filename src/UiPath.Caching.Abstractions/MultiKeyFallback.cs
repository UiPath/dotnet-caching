namespace UiPath.Caching;

/// <summary>What the batch members do on a cache without the multi-key capability: chunks of single-key calls, each chunk started before it is awaited.</summary>
internal static class MultiKeyFallback
{
    private const int ChunkSize = 256;

    internal static async ValueTask<IReadOnlyList<CacheKey>> RunAsync<TItem, TState>(IEnumerable<TItem> items, TState state, Func<TItem, CacheKey> keyOf, Func<TState, TItem, ValueTask<bool>> start)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<CacheKey>? failed = null;
        var chunk = new List<TItem>(ChunkSize);
        foreach (var item in items)
        {
            chunk.Add(item);
            if (chunk.Count == ChunkSize)
            {
                await RunChunkAsync(chunk, state, keyOf, start, failed ??= []).ConfigureAwait(false);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            await RunChunkAsync(chunk, state, keyOf, start, failed ??= []).ConfigureAwait(false);
        }

        return failed is null ? [] : failed;
    }

    private static async ValueTask RunChunkAsync<TItem, TState>(List<TItem> chunk, TState state, Func<TItem, CacheKey> keyOf, Func<TState, TItem, ValueTask<bool>> start, List<CacheKey> failed)
    {
        var pending = new ValueTask<bool>[chunk.Count];
        var startFailed = new bool[chunk.Count];
        for (var i = 0; i < chunk.Count; i++)
        {
            try
            {
#pragma warning disable CA2012 // started together so they share the pipeline, each awaited exactly once below
                pending[i] = start(state, chunk[i]);
#pragma warning restore CA2012
            }
            catch
            {
                startFailed[i] = true;
            }
        }

        for (var i = 0; i < chunk.Count; i++)
        {
            var ok = false;
            if (!startFailed[i])
            {
                try
                {
                    ok = await pending[i].ConfigureAwait(false);
                }
                catch
                {
                    // A call that throws counts as failed, as one that returns false does.
                }
            }

            if (!ok)
            {
                failed.Add(keyOf(chunk[i]));
            }
        }
    }
}
