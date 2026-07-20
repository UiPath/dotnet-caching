namespace UiPath.Caching;

/// <summary>
/// <see cref="ISetCacheProvider"/> for the multilayer <see cref="ISetCache"/>. Mirrors
/// <c>InMemoryRedisCacheProvider</c>: it composes a local in-process snapshot (L1) in front of the
/// Redis set cache (L2), obtaining the Redis tier from the <see cref="IQueueCacheFactory"/>
/// (<see cref="KnownCacheProviderNames.Redis"/>) rather than constructing it by hand. The factory is
/// injected as a <see cref="Func{TResult}"/> so it can be resolved lazily — the factory is built from
/// the providers, so a direct dependency would be a construction-time cycle.
/// </summary>
public sealed class InMemoryRedisSetCacheProvider : ISetCacheProvider
{
    private readonly Lazy<MultilayerSetCache> _cache;

    public InMemoryRedisSetCacheProvider(
        Func<IQueueCacheFactory> queueCacheFactory,
        IMemoryCacheFactory memoryCacheFactory,
        ISerializerProxy<RedisValue> serializer,
        IOptions<InMemoryRedisSetCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(queueCacheFactory);
        ArgumentNullException.ThrowIfNull(memoryCacheFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(options);
        Enabled = options.Value.Enabled;
        var factory = new Lazy<IQueueCacheFactory>(queueCacheFactory);
        _cache = new Lazy<MultilayerSetCache>(() =>
        {
            var l2 = factory.Value.CreateSetCache(KnownCacheProviderNames.Redis);
            return new MultilayerSetCache(Name, l2, memoryCacheFactory, serializer, options.Value, options.Value.LocalMaxExpiration,
                options.Value.ConnectionMonitorEnabled, options.Value.ConnectionMonitorPeriod,
                options.Value.UseLocalOnlyWhenDisconnected, options.Value.LocalMaxExpirationDisconnected);
        });
    }

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled { get; }

    public ISetCache CreateSetCache() => _cache.Value;

    public void Dispose()
    {
        if (_cache.IsValueCreated)
        {
            _cache.Value.Dispose();
        }
    }
}
