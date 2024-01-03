using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisCacheProvider : ICacheProvider
{
    private readonly RedisCacheOptions _redisCacheOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IRedisConnector _redis;
    private readonly ISerializerProxy _serializerProxy;
    private readonly IPolicyHolder _policyHolder;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<RedisCache> _cache;
    private readonly Lazy<RedisHashCache> _hashCache;

    public string Name => KnownCacheProviderNames.Redis;

    public bool Enabled { get; }

    public RedisCacheProvider(
        IOptions<RedisConnectionOptions> connectionOptionsAccessor,
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IRedisConnector redis,
        ISerializerProxy serializerProxy,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory)
    {
        _redisCacheOptions = redisCacheOptions.Value;
        _cacheOptions = cacheOptions.Value;
        _redis = redis;
        _serializerProxy = serializerProxy;
        _policyHolder = policyHolder;
        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory;
        _cache = new Lazy<RedisCache>(() => BuildCache());
        _hashCache = new Lazy<RedisHashCache>(() => BuildHashCache());
        Enabled = connectionOptionsAccessor.Value.Enabled && _redisCacheOptions.Enabled;
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

    private RedisCache BuildCache() =>
        new(
            _redis,
            _serializerProxy,
            _policyHolder,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _loggerFactory.Create<RedisCache>());

    private RedisHashCache BuildHashCache() =>
        new(
            _redis,
            _serializerProxy,
            _policyHolder,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _loggerFactory.Create<RedisHashCache>());
}
