using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

public sealed class InMemoryRedisCacheProvider : ICacheProvider
{
    private readonly InMemoryRedisCacheOptions _options;
    private readonly CacheOptions _cacheOptions;
    private readonly IMemoryCacheFactory _memoryCacheFactory;
    private readonly IChangeTokenFactory _changeTokenFactory;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;

    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<ICacheFactory> _cacheFactory;
    private readonly ILocalLock _localLock;
    private readonly IDistributedLock _distributedLock;
    private readonly ICachePolicyFactory _policyFactory;
    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled { get; }

    public InMemoryRedisCacheProvider(
        IOptions<InMemoryRedisCacheOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IMemoryCacheFactory memoryCacheFactory,
        Func<ICacheFactory> cacheFactoryAccessor,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory)
    {
        _options = optionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _cacheFactory = new Lazy<ICacheFactory>(cacheFactoryAccessor);
        _changeTokenFactory = changeTokenFactory;
        _topicFactory = topicFactory;
        _cacheEventFactory = cacheEventFactory;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _loggerFactory = loggerFactory;
        _localLock = localLock;
        _distributedLock = distributedLock;
        _policyFactory = policyFactory;
        _cache = new Lazy<MultilayerCache>(() => BuildCache());
        _hashCache = new Lazy<MultilayerHashCache>(() => BuildHashCache());
        Enabled = _options.Enabled;
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
            _cacheFactory.Value.CreateCache(KnownCacheProviderNames.Redis),
            _memoryCacheFactory,
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
            _options,
            _cacheOptions,
            localLock: _localLock,
            distributedLock: _distributedLock,
            policyFactory: _policyFactory,
            logger: _loggerFactory.CreateLogger($"{Name}.Cache"));

    private MultilayerHashCache BuildHashCache() =>
        new(
            Name,
            _cacheFactory.Value.CreateHashCache(KnownCacheProviderNames.Redis),
            _memoryCacheFactory,
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
            _options,
            _cacheOptions,
            localLock: _localLock,
            distributedLock: _distributedLock,
            policyFactory: _policyFactory,
            logger: _loggerFactory.CreateLogger($"{Name}.HashCache"));
}
