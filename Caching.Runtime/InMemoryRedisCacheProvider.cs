using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed class InMemoryRedisCacheProvider : ICacheProvider
{
    private readonly InMemoryRedisCacheOptions _options;
    private readonly IMemoryCacheFactory _memoryCacheFactory;
    private readonly IChangeTokenFactory _changeTokenFactory;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;

    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<ICacheFactory> _cacheFactory;
    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled { get; }

    

    public InMemoryRedisCacheProvider(
        IOptions<RedisConnectionOptions> connectionOptionsAccessor,
        IOptions<InMemoryRedisCacheOptions> optionsAccessor,
        IMemoryCacheFactory memoryCacheFactory,
        Func<ICacheFactory> cacheFactoryAccessor,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory)
    {
        _options = optionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _cacheFactory = new Lazy<ICacheFactory>(cacheFactoryAccessor);
        _changeTokenFactory = changeTokenFactory;
        _topicFactory = topicFactory;
        _cacheEventFactory = cacheEventFactory;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _loggerFactory = loggerFactory;
        _cache = new Lazy<MultilayerCache>(() => BuildCache());
        _hashCache = new Lazy<MultilayerHashCache>(() => BuildHashCache());
        Enabled = _options.Enabled && connectionOptionsAccessor.Value.Enabled;
    }

    public ICache CreateCache() =>
        _cache.Value;

    public IHashCache CreateHashCache() =>
        _hashCache.Value;

    public void Dispose()
    {
        if (_cache.IsValueCreated)
        {
            _cache.Value.Dispose();
        }

        if (_hashCache.IsValueCreated)
        {
            _hashCache.Value.Dispose();
        }
    }

    private MultilayerCache BuildCache() =>
        new(
            Name,
            _cacheFactory.Value.CreateCache(KnownCacheProviderNames.Redis, callerType: GetType()),
            () => _memoryCacheFactory.Get(_options),
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
            _loggerFactory.CreateLogger($"{Name}.Cache"));

    private MultilayerHashCache BuildHashCache() =>
        new(
            Name,
            _cacheFactory.Value.CreateHashCache(KnownCacheProviderNames.Redis, callerType: GetType()),
            () => _memoryCacheFactory.Get(_options),
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
             _loggerFactory.CreateLogger($"{Name}.HashCache"));
}
