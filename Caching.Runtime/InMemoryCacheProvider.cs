using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;
public sealed class InMemoryCacheProvider : ICacheProvider
{
    private readonly InMemoryCacheOptions _options;
    private readonly Func<IMemoryStatisticsOptions, IMemoryCache> _memoryCacheAccessor;
    private readonly IChangeTokenFactory _changeTokenFactory;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Lazy<MultilayerCache> _cache;
    private readonly Lazy<MultilayerHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.InMemory;

    public bool Enabled => _options.Enabled;

    public InMemoryCacheProvider(
        IOptions<InMemoryCacheOptions> optionsAccessor,
        Func<IMemoryStatisticsOptions, IMemoryCache> memoryCacheAccessor,
        ICacheEventFactory? cacheEventFactory = null,
        IChangeTokenFactory? changeTokenFactory = null,
        ITopicFactory? topicFactory = null,
        ICachingTelemetryProvider? cachingTelemetryProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = optionsAccessor.Value;
        _memoryCacheAccessor = memoryCacheAccessor;
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


        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
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
            NullHashCache.Instance,
            () => _memoryCacheAccessor(_options),
            _changeTokenFactory,
            _topicFactory,
            _cacheEventFactory,
            _cachingTelemetryProvider,
            _options,
           _loggerFactory.CreateLogger($"{Name}.HashCache"));
}
