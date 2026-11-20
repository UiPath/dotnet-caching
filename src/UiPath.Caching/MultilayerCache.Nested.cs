namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>What the batch rehydrate write needs to know about one state.</summary>
    private readonly record struct RehydrateTarget(CacheEntryOptions Options, DateTimeOffset Expiration, CacheKey CallerKey);

    private readonly struct CacheEntryValue<T>(CacheEntryOptions cacheEntry, T? value)
    {
        public CacheEntryOptions CacheEntry { get; init; } = cacheEntry;
        public T? Value { get; init; } = value;
    }

    private readonly record struct Miss<T>(CacheKey Key, CacheEntryOptions Options, Func<CancellationToken, Task<T?>> Generator, TimeSpan Duration, TimeSpan? RehydrateJitter, CachePolicy Policy, long Seen, bool InnerMissed);

    private sealed record BatchRun(int Index, IInFlightReservation<object?> Run);

    private sealed record BatchJoin<T>(int Index, InFlight<InFlightKey, object?>.Joined Join, Task<ICacheEntry<T?>> Run);

    /// <summary>Reads and keeps the refreshed entries of one value type.</summary>
    private sealed class RefreshReader<T> : IPeerRefreshReader
    {
        public static readonly RefreshReader<T> Instance = new();

        public async ValueTask<PeerRead[]> ReadAsync(MultilayerCacheBase cache, CacheKey[] keys, CachePolicy policy, CancellationToken token)
        {
            var inner = ((MultilayerCache)cache)._innerCache;
            if (keys.Length == 1)
            {
                var entry = await inner.GetCacheEntryAsync<T>(keys[0], policy, token).ConfigureAwait(false);
                return [new PeerRead(entry.Found, entry)];
            }

            var entries = await inner.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);
            return Array.ConvertAll(entries, static e => new PeerRead(e.Value.Found, e.Value));
        }

        public PeerKeep Keep(MultilayerCacheBase cache, RefreshMetadataState held, PeerRead read, long mark, out object? installed)
        {
            var owner = (MultilayerCache)cache;
            var entry = (ICacheEntry<T?>)read.Entry!;
            var options = new CacheEntryOptions
            {
                CacheKey = held.CacheKey,
                CallerKey = held.CallerKey,
                TopicKey = held.TopicKey,
                Expiration = entry.Expiration,
            };
            var name = held.CacheKey.Name;
            var policy = held.Refresh!.Value.Policy;
            object? kept = null;
            var failed = false;
            // Only over the entry the event hit: a write, removal or loss since then replaced or dropped it.
            var filled = owner._background.Fill(name, mark, owner, o =>
            {
                if (!o.IsLocalEntry(name, held.CacheEntity))
                {
                    return false;
                }

                var item = o._cacheEntryFactory.Create(entry.Value, options.Expiration);
                InheritRead(held.CacheEntity, item);
                LinkRefresh(held.CacheEntity, item);
                if (!o.MemorySet(options, entry.Value, o.LocalLifetime(policy), item, policy, read: false))
                {
                    failed = true;
                    return false;
                }

                // Read back under the fill's stripe lock, before a later write can replace it.
                o._memoryCache.TryGetValue(name, out kept);
                return true;
            });
            installed = kept;
            return filled switch
            {
                true => PeerKeep.Kept,
                false when failed => PeerKeep.Failed,
                false => PeerKeep.Replaced,
                null => owner.IsLocalEntry(name, held.CacheEntity) ? PeerKeep.Declined : PeerKeep.Replaced,
            };
        }
    }
}
