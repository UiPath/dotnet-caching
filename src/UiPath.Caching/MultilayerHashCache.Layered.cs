namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    private LayeredEntry<HashShape<T>, IDictionary<string, T?>, InternalHashCacheEntryOptions, InnerWrite> Layered<T>() => new(this, new HashShape<T>(this));

    private readonly struct HashShape<T>(MultilayerHashCache cache) : ILayeredShape<IDictionary<string, T?>, InternalHashCacheEntryOptions, InnerWrite>
    {
        public Type EntryType => typeof(T);

        public string Kind => "hash";

        public bool InnerIsNull => cache._innerCache is NullHashCache;

        public bool IsEmpty(IDictionary<string, T?>? value) => IsNullOrEmpty(value);

        public IDictionary<string, T?> Present(IDictionary<string, T?>? value) => value ?? Empty<T>();

        public InternalHashCacheEntryOptions BuildOptions(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) =>
            cache._entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, HashCacheSetOption.KeyReplace, token);

        public ValueTask<ICacheEntry<IDictionary<string, T?>>> ReadInnerAsync(InternalHashCacheEntryOptions options, CachePolicy policy) =>
            cache._innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token);

        public void Adopt(ref InternalHashCacheEntryOptions options, ICacheEntry<IDictionary<string, T?>> fetched)
        {
            options.Expiration = fetched.Expiration;
            options.Metadata = fetched.Metadata;
            options.Version = cache._versioned is null ? 0 : HashVersions.NumberOf(fetched);
        }

        public void LogFoundInner(InternalHashCacheEntryOptions options) => cache.LogFoundInnerCopy(cache.Logged(options, typeof(T)));

        public bool KeepLocal(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration) => cache.MemorySet(options, value, maxExpiration);

        public bool KeepWhileDisconnected(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration, ICacheEntry<IDictionary<string, T?>>? entry) =>
            cache.MemorySet(options, value, maxExpiration, keepWhenAbsent: true);

        public bool KeepWritten(BackgroundWrites.Keeper keeper, InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration, ICacheEntry<IDictionary<string, T?>>? entry, long version)
        {
            options.Version = version;
            return keeper.Local(options.CacheKey.Name, (Cache: cache, Options: options, Value: value, Max: maxExpiration), static s => s.Cache.MemorySet(s.Options, s.Value, s.Max), static s => s.Cache.DropSuperseded(s.Options.CacheKey.Name), options.Version);
        }

        public bool Written(InnerWrite write) => write.Written;

        public VersionedWrite Counted(InnerWrite write) => write.Counted;

        public long Version(InnerWrite write) => write.Version;

        [AsyncMethodBuilder(typeof(Pooled<>))]
        public async ValueTask<InnerWrite> WriteInnerAsync(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, CachePolicy policy, bool keepRefused, BackgroundWrites.Keeper keeper)
        {
            VersionedWrite counted = default;
            var version = options.Version;
            bool ret;
            try
            {
                if (cache._versioned is { } versioned)
                {
                    counted = await versioned.SetVersionedAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, cache._peerLimits, options.Token).ConfigureAwait(false);
                    ret = counted.Written;
                    version = counted.Written ? counted.Version : 0;
                    keeper.Returned(options.CacheKey.Name, version);
                }
                else
                {
                    ret = await cache._innerCache.SetAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, options.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && options.Token.IsCancellationRequested))
            {
                cache.LogInnerCacheSetError(ex, cache.Logged(options, typeof(T)));
                ret = false;
            }

            return new(ret, counted, version);
        }

        public ValueTask<bool> AnnounceSetAsync(InternalHashCacheEntryOptions options, VersionedWrite counted) =>
            counted.Fields is { } fields && counted.Version > 0
                ? cache._eventPublisher.CacheSetAsync(options, typeof(T), counted.Version, fields)
                : cache._eventPublisher.CacheSetAsync(options, typeof(T));
    }
}
