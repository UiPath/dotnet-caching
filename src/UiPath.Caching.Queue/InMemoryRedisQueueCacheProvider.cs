using UiPath.Caching.Locking;

namespace UiPath.Caching;

/// <summary>
/// <see cref="IQueueCacheProvider"/> for the multilayer backing. Mirrors
/// <c>InMemoryRedisCacheProvider</c>: it composes a local in-process snapshot (L1) in front of the
/// Redis set cache (L2), obtaining the Redis tier from the <see cref="IQueueCacheFactory"/>
/// (<see cref="KnownCacheProviderNames.Redis"/>) rather than constructing it by hand. The factory is
/// injected as a <see cref="Func{TResult}"/> so it can be resolved lazily — the factory is built from
/// the providers, so a direct dependency would be a construction-time cycle.
/// </summary>
public sealed class InMemoryRedisQueueCacheProvider : IQueueCacheProvider
{
    private readonly InMemoryRedisQueueCacheOptions _options;
    private readonly IMemoryCacheFactory _memoryCacheFactory;
    private readonly Lazy<IQueueCacheFactory> _queueCacheFactory;
    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly ILocalLock _localLock;
    private readonly Lazy<MultilayerSetCache> _setCache;

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled { get; }

    public InMemoryRedisQueueCacheProvider(
        IOptions<InMemoryRedisQueueCacheOptions> optionsAccessor,
        IMemoryCacheFactory memoryCacheFactory,
        Func<IQueueCacheFactory> queueCacheFactoryAccessor,
        ISerializerProxy<RedisValue> serializer,
        ILocalLock localLock)
    {
        _options = optionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _queueCacheFactory = new Lazy<IQueueCacheFactory>(queueCacheFactoryAccessor);
        _serializer = serializer;
        _localLock = localLock;
        _setCache = new Lazy<MultilayerSetCache>(() => BuildSetCache());
        Enabled = _options.Enabled;
    }

    public ISetCache CreateSetCache() =>
        _setCache.Value;

    public void Dispose()
    {
        if (_setCache.IsValueCreated)
        {
            _setCache.Value.Dispose();
        }
    }

    private MultilayerSetCache BuildSetCache() =>
        new(
            Name,
            _queueCacheFactory.Value.CreateSetCache(KnownCacheProviderNames.Redis),
            _memoryCacheFactory,
            _serializer,
            _options,
            _localLock,
            _options.LocalMaxExpiration,
            _options.ConnectionMonitorEnabled,
            _options.ConnectionMonitorPeriod,
            _options.UseLocalOnlyWhenDisconnected,
            _options.LocalMaxExpirationDisconnected);
}
