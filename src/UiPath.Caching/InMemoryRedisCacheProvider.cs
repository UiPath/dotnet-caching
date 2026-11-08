using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

public sealed partial class InMemoryRedisCacheProvider : ICacheProvider
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
    private readonly IDistributedLock _distributedLock;
    private readonly ICachePolicyFactory _policyFactory;
    private readonly TimeProvider _clock;
    private readonly IKeyMaskingPolicy? _keyMaskingPolicy;
    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

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
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
    {
        _keyMaskingPolicy = keyMaskingPolicy;
        _clock = clock;
        _options = optionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _cacheFactory = new Lazy<ICacheFactory>(cacheFactoryAccessor);
        if (_options.BroadcastEnable)
        {
            _changeTokenFactory = changeTokenFactory;
            _topicFactory = topicFactory;
            _cacheEventFactory = cacheEventFactory;
        }
        else
        {
            _changeTokenFactory = NullChangeTokenFactory.Instance;
            _topicFactory = NullTopicFactory.Instance;
            _cacheEventFactory = NullCacheEventFactory.Instance;
        }
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _loggerFactory = loggerFactory;
        _distributedLock = distributedLock;
        _policyFactory = policyFactory;
        _cache = new Lazy<MultilayerCache>(() => BuildCache());
        _hashCache = new Lazy<MultilayerHashCache>(() => BuildHashCache());
        Enabled = _options.Enabled;
        if (Enabled && !IsCap(_options.LocalMaxExpiration) && !IsCap(policyFactory.Default?.LocalExpiration))
        {
            LogUncappedLocalTier(loggerFactory.CreateLogger(Name));
        }
    }

    public string Name => KnownCacheProviderNames.InMemoryRedis;

    public bool Enabled { get; }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The InMemoryRedis local tier has no lifetime cap: neither InMemoryRedis LocalMaxExpiration nor the default policy's LocalExpiration is set. If a peer's invalidation is lost, a local copy can outlive what the application expects; set either one to bound it. A named policy's own LocalExpiration bounds only the caches that use it.")]
    private static partial void LogUncappedLocalTier(ILogger logger);

    private bool IsCap(TimeSpan? lifetime) =>
        lifetime.HasValue && _clock.ToDateTimeOffset(lifetime) != DateTimeOffset.MaxValue;

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
            distributedLock: _distributedLock,
            policyFactory: _policyFactory,
            clock: _clock,
            logger: _loggerFactory.CreateLogger($"{Name}.Cache"),
            keyMaskingPolicy: _keyMaskingPolicy);

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
            distributedLock: _distributedLock,
            policyFactory: _policyFactory,
            clock: _clock,
            logger: _loggerFactory.CreateLogger($"{Name}.HashCache"),
            keyMaskingPolicy: _keyMaskingPolicy);
}
