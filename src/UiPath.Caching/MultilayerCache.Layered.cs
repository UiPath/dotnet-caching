namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    private LayeredEntry<CacheShape<T>, T?, CacheEntryOptions, bool> Layered<T>() => new(this, new CacheShape<T>(this));

    private readonly struct CacheShape<T>(MultilayerCache cache) : ILayeredShape<T?, CacheEntryOptions, bool>
    {
        public Type EntryType => typeof(T);

        public string Kind => "cache";

        public bool InnerIsNull => !cache._sharedInner;

        public bool IsEmpty(T? value) => value is null;

        public T? Present(T? value) => value;

        public CacheEntryOptions BuildOptions(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) =>
            cache._entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        public ValueTask<ICacheEntry<T?>> ReadInnerAsync(CacheEntryOptions options, CachePolicy policy) =>
            cache._innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token);

        public void Adopt(ref CacheEntryOptions options, ICacheEntry<T?> fetched) => options.Expiration = fetched.Expiration;

        public void LogFoundInner(CacheEntryOptions options) => cache.LogFoundInnerCacheCopy(cache.Logged(options, typeof(T)));

        public bool KeepLocal(CacheEntryOptions options, T? value, TimeSpan? maxExpiration) => cache.MemorySet(options, value, maxExpiration);

        public bool KeepWhileDisconnected(CacheEntryOptions options, T? value, TimeSpan? maxExpiration, ICacheEntry<T?>? entry) =>
            cache.MemorySet(options, value, maxExpiration, entry);

        public bool KeepWritten(BackgroundWrites.Keeper keeper, CacheEntryOptions options, T? value, TimeSpan? maxExpiration, ICacheEntry<T?>? entry, long version) =>
            keeper.Local(options.CacheKey.Name, (Cache: cache, Options: options, Value: value, Max: maxExpiration, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Value, s.Max, s.Entry));

        public bool Written(bool write) => write;

        public VersionedWrite Counted(bool write) => default;

        public long Version(bool write) => 0;

        [AsyncMethodBuilder(typeof(Pooled<>))]
        public async ValueTask<bool> WriteInnerAsync(CacheEntryOptions options, T? value, CachePolicy policy, bool keepRefused, BackgroundWrites.Keeper keeper)
        {
            try
            {
                return await cache._innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && options.Token.IsCancellationRequested))
            {
                cache.LogInnerCacheSetError(ex, cache.Logged(options, typeof(T)));
                return false;
            }
        }

        public ValueTask<bool> AnnounceSetAsync(CacheEntryOptions options, VersionedWrite counted) => cache._eventPublisher.CacheSetAsync(options, typeof(T));
    }
}
