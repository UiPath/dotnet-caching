using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;
public sealed class InMemoryCacheProvider : ICacheProvider
{
    private readonly InMemoryCacheOptions _options;
    private readonly CacheOptions _cacheOptions;
    private readonly IMemoryCacheFactory _memoryCacheFactory;
    private readonly IChangeTokenFactory _changeTokenFactory;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILocalLock _localLock;
    private readonly ICachePolicyFactory _policyFactory;

    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.InMemory;

    public bool Enabled => _options.Enabled;

    public InMemoryCacheProvider(
        IOptions<InMemoryCacheOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IMemoryCacheFactory memoryCacheFactory,
        ICacheEventFactory cacheEventFactory,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ILocalLock localLock,
        ICachePolicyFactory policyFactory)
    {
        _options = optionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _changeTokenFactory = NullChangeTokenFactory.Instance;
        _topicFactory = NullTopicFactory.Instance;
        _cacheEventFactory = NullCacheEventFactory.Instance;
        if (_options.BroadcastEnable)
        {
            if (changeTokenFactory != null)
            {
                _changeTokenFactory = changeTokenFactory;
            }

            if (topicFactory != null)
            {
                _topicFactory = topicFactory;
            }

            if (cacheEventFactory != null)
            {
                _cacheEventFactory = cacheEventFactory;
            }
        }

        _cachingTelemetryProvider = cachingTelemetryProvider;
        _loggerFactory = loggerFactory;
        _localLock = localLock;
        _policyFactory = policyFactory;
        _cache = new Lazy<MultilayerCache>(BuildCache);
        _hashCache = new Lazy<MultilayerHashCache>(BuildHashCache);
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
            NullCache.Instance,
            _memoryCacheFactory,
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
            _options,
            _cacheOptions,
            localLock: _localLock,
            distributedLock: NullDistributedLock.Instance,
            policyFactory: _policyFactory,
            logger: _loggerFactory.CreateLogger($"{Name}.Cache"));

    private MultilayerHashCache BuildHashCache() =>
        new(
            Name,
            NullHashCache.Instance,
            _memoryCacheFactory,
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
            _options,
            _cacheOptions,
            localLock: _localLock,
            distributedLock: NullDistributedLock.Instance,
            policyFactory: _policyFactory,
            logger: _loggerFactory.CreateLogger($"{Name}.HashCache"));
}
