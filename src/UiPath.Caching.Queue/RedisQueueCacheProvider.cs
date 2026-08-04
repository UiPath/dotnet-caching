namespace UiPath.Caching;

/// <summary>
/// <see cref="IQueueCacheProvider"/> for the Redis backing. Mirrors <c>RedisCacheProvider</c>;
/// lazily builds a <see cref="Redis.RedisSetCache"/> over the shared Redis connection.
/// </summary>
public sealed class RedisQueueCacheProvider : IQueueCacheProvider
{
    private readonly RedisCacheOptions _redisCacheOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly RedisSetCacheOptions _setCacheOptions;
    private readonly IRedisConnector _redis;
    private readonly ISerializerProxy<RedisValue> _serializerProxy;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICachePolicyFactory _policyFactory;
    private readonly Lazy<RedisSetCache> _setCache;

    public string Name => KnownCacheProviderNames.Redis;

    public bool Enabled { get; }

    public RedisQueueCacheProvider(
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IOptions<RedisSetCacheOptions> setCacheOptions,
        IRedisConnector redis,
        ISerializerProxy<RedisValue> serializerProxy,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ICachePolicyFactory policyFactory)
    {
        _redisCacheOptions = redisCacheOptions.Value;
        _cacheOptions = cacheOptions.Value;
        _setCacheOptions = setCacheOptions.Value;
        _redis = redis;
        _serializerProxy = serializerProxy;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _policyFactory = policyFactory;
        _setCache = new Lazy<RedisSetCache>(() => BuildSetCache());
        Enabled = _setCacheOptions.Enabled;
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

    private RedisSetCache BuildSetCache() =>
        new(
            _redis,
            _serializerProxy,
            _resiliencePipelineProvider,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _setCacheOptions,
            _policyFactory,
            _loggerFactory.CreateLogger<RedisSetCache>());
}
