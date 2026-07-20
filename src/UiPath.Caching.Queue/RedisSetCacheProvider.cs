namespace UiPath.Caching;

/// <summary>
/// <see cref="ISetCacheProvider"/> for the Redis-backed <see cref="ISetCache"/>. Mirrors
/// <c>RedisCacheProvider</c>; lazily builds a <see cref="Redis.RedisSetCache"/> over the shared
/// Redis connection.
/// </summary>
public sealed class RedisSetCacheProvider : ISetCacheProvider
{
    private readonly Lazy<RedisSetCache> _cache;

    public RedisSetCacheProvider(
        IRedisConnector redis,
        ISerializerProxy<RedisValue> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IOptions<RedisSetCacheOptions> setCacheOptions,
        ICachePolicyFactory policyFactory,
        ICachingTelemetryProvider? telemetryProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        Enabled = setCacheOptions.Value.Enabled;
        _cache = new Lazy<RedisSetCache>(() => new RedisSetCache(
            redis,
            serializer,
            resiliencePipelineProvider,
            telemetryProvider ?? NullTelemetryProvider.Instance,
            redisCacheOptions.Value,
            cacheOptions.Value,
            setCacheOptions.Value,
            policyFactory,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RedisSetCache>()));
    }

    public string Name => KnownCacheProviderNames.Redis;

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
