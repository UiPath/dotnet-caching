using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed class InMemoryRedisCacheProvider : ICacheProvider
{
    private readonly InMemoryRedisCacheOptions _options;
    private readonly Func<IMemoryStatisticsOptions, IMemoryCache> _memoryCacheAccessor;
    private readonly IChangeTokenFactory _changeTokenFactory;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;

    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<ICacheFactory> _cacheFactory;
    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled => _options.Enabled;

    

    public InMemoryRedisCacheProvider(IOptions<InMemoryRedisCacheOptions> optionsAccessor,
        Func<IMemoryStatisticsOptions, IMemoryCache> memoryCacheAccessor,
        Func<ICacheFactory> cacheFactoryAccessor,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider? cachingTelemetryProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = optionsAccessor.Value;
        _memoryCacheAccessor = memoryCacheAccessor;
        _cacheFactory = new Lazy<ICacheFactory>(cacheFactoryAccessor);
        _changeTokenFactory = changeTokenFactory;
        _topicFactory = topicFactory;
        _cacheEventFactory = cacheEventFactory;
        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _cache = new Lazy<MultilayerCache>(() => BuildCache());
        _hashCache = new Lazy<MultilayerHashCache>(() => BuildHashCache());
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
            () => _memoryCacheAccessor(_options),
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
            () => _memoryCacheAccessor(_options),
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
             _loggerFactory.CreateLogger($"{Name}.HashCache"));
}
