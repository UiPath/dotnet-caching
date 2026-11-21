namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    private LayeredEntry<CacheShape<T>, T?, CacheEntryOptions, bool> Layered<T>() => new(this, new CacheShape<T>(this));

    private readonly struct CacheShape<T>(MultilayerCache cache) : ILayeredShape<T?, CacheEntryOptions, bool>
    {
        public Type EntryType => typeof(T);

        public string Kind => "cache";

        public bool InnerIsNull => !cache._sharedInner;

        public bool CanAdopt => cache._innerCache is IMigrationCache;

        public bool CanStamp => cache._innerCache is IStampedTier;

        public bool IsEmpty(T? value) => value is null;

        public T? Present(T? value) => value;

        public CacheEntryOptions BuildOptions(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) =>
            cache._entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        public ValueTask<ICacheEntry<T?>> ReadInnerAsync(CacheEntryOptions options, CachePolicy policy) =>
            cache._innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token);

        public ValueTask<ICacheEntry<T?>> ReadForAdoptionAsync(CacheEntryOptions options, CachePolicy policy, StampBox stamp) =>
            cache._innerCache is IStampedTier tier
                ? ReadStampedForAdoptionAsync(tier, options, policy, stamp)
                : AdoptionEntryAsync(((IMigrationCache)cache._innerCache).ReadSnapshotAsync<T>(options.CacheKey, policy, options.Token));

        public async ValueTask<StampedWrite> StoreStampedAsync(CacheEntryOptions options, T? value, CachePolicy policy, StampBox stamp)
        {
            try
            {
                return await cache._background.WriteAsync(
                    options.CacheKey.Name,
                    (Cache: cache, Shape: this, Options: options, Value: value, Policy: policy, Stamp: stamp),
                    null,
                    static (s, _) => ((IStampedTier)s.Cache._innerCache).WriteIfStampAsync<T?>(s.Options.CacheKey, s.Value, s.Options.Expiration, s.Policy, s.Stamp.Stamp, s.Options.Token),
                    static (s, written, keeper) =>
                    {
                        if (written == StampedWrite.Won)
                        {
                            s.Cache.KeepAfterInnerWrite(true, false, s.Policy, (s.Shape, s.Options, s.Value, Keeper: keeper, s.Policy), static (x, max) => x.Shape.KeepWritten(x.Keeper, x.Options, x.Value, max, null, 0, x.Policy), s.Options.Token);
                        }

                        return written;
                    },
                    options.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
            {
                return StampedWrite.Failed;
            }
            catch (Exception ex)
            {
                cache.LogInnerCacheSetError(ex, cache.Logged(options, typeof(T)));
                return StampedWrite.Failed;
            }
        }


        public void Adopt(ref CacheEntryOptions options, ICacheEntry<T?> fetched) => options.Expiration = fetched.Expiration;

        public void LogFoundInner(CacheEntryOptions options) => cache.LogFoundInnerCacheCopy(cache.Logged(options, typeof(T)));

        public bool KeepLocal(CacheEntryOptions options, T? value, TimeSpan? maxExpiration, CachePolicy policy) => cache.MemorySet(options, value, maxExpiration, null, policy, read: true);

        public bool KeepWhileDisconnected(CacheEntryOptions options, T? value, TimeSpan? maxExpiration, ICacheEntry<T?>? entry, CachePolicy policy) =>
            cache.MemorySet(options, value, maxExpiration, entry, read: true, failSafe: policy);

        public bool KeepWritten(BackgroundWrites.Keeper keeper, CacheEntryOptions options, T? value, TimeSpan? maxExpiration, ICacheEntry<T?>? entry, long version, CachePolicy policy) =>
            keeper.Local(options.CacheKey.Name, (Cache: cache, Options: options, Value: value, Max: maxExpiration, Entry: entry, Policy: policy), static s => s.Cache.MemorySet(s.Options, s.Value, s.Max, s.Entry, s.Policy, read: true));

        public bool Written(bool write) => write;

        public VersionedWrite Counted(bool write) => default;

        public long Version(bool write) => 0;

        [AsyncMethodBuilder(typeof(Pooled<>))]
        public async ValueTask<bool> WriteInnerAsync(CacheEntryOptions options, T? value, CachePolicy policy, bool keepRefused, BackgroundWrites.Keeper keeper)
        {
            try
            {
                // A late fail-safe value checks in the key's turn that nothing removed or replaced the key since its generator started.
                if (!cache.LateStillCurrent(options))
                {
                    return false;
                }

                return await cache._innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!keepRefused && options.Token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && options.Token.IsCancellationRequested))
            {
                cache.LogInnerCacheSetError(ex, cache.Logged(options, typeof(T)));
                return false;
            }
        }

        public ValueTask<bool> AnnounceSetAsync(CacheEntryOptions options, VersionedWrite counted) => cache._eventPublisher.CacheSetAsync(options, typeof(T));

        private static async ValueTask<ICacheEntry<T?>> ReadStampedForAdoptionAsync(IStampedTier tier, CacheEntryOptions options, CachePolicy policy, StampBox stamp)
        {
            var (snapshot, seen) = await tier.ReadStampedAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);
            stamp.Stamp = seen;
            stamp.Read = snapshot.Outcome != SnapshotOutcome.Failed;
            return await AdoptionEntryAsync(new ValueTask<SnapshotRead<T>>(snapshot)).ConfigureAwait(false);
        }
    }
}
