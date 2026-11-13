using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class RedisCacheProvider : ICacheProvider
{
    private readonly RedisCacheOptions _redisCacheOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IRedisConnector _redis;
    private readonly ISerializerProxy<byte[]> _serializerProxy;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICachePolicyFactory _policyFactory;
    private readonly TimeProvider _clock;
    private readonly IKeyMaskingPolicy? _keyMaskingPolicy;
    private readonly ValueSerializerRegistry _valueSerializers;
    private readonly Lazy<RedisCache> _cache;
    private readonly Lazy<RedisHashCache> _hashCache;

    public RedisCacheProvider(
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializerProxy,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : this(
            redisCacheOptions,
            cacheOptions,
            redis,
            serializerProxy,
            resiliencePipelineProvider,
            cachingTelemetryProvider,
            loggerFactory,
            policyFactory,
            clock,
            keyMaskingPolicy,
            ValueSerializerRegistry.Empty)
    {
    }

    internal RedisCacheProvider(
        IOptions<RedisCacheOptions> redisCacheOptions,
        IOptions<CacheOptions> cacheOptions,
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializerProxy,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider cachingTelemetryProvider,
        ILoggerFactory loggerFactory,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy,
        ValueSerializerRegistry valueSerializers)
    {
        _valueSerializers = valueSerializers;
        _keyMaskingPolicy = keyMaskingPolicy;
        _clock = clock;
        _redisCacheOptions = redisCacheOptions.Value;
        _cacheOptions = cacheOptions.Value;
        _redis = redis;
        _serializerProxy = serializerProxy;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _cachingTelemetryProvider = cachingTelemetryProvider ?? NullTelemetryProvider.Instance;
        _loggerFactory = loggerFactory;
        _policyFactory = policyFactory;
        _cache = new Lazy<RedisCache>(() => BuildCache());
        _hashCache = new Lazy<RedisHashCache>(() => BuildHashCache());
        Enabled = _redisCacheOptions.Enabled;
    }

    public string Name => KnownCacheProviderNames.Redis;

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

    internal static RedisCacheProvider Create(IServiceProvider services) =>
        new(
            services.GetRequiredService<IOptions<RedisCacheOptions>>(),
            services.GetRequiredService<IOptions<CacheOptions>>(),
            services.GetRequiredService<IRedisConnector>(),
            services.GetRequiredService<ISerializerProxy<byte[]>>(),
            services.GetRequiredService<IResiliencePipelineProvider>(),
            services.GetRequiredService<ICachingTelemetryProvider>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<ICachePolicyFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetService<IKeyMaskingPolicy>(),
            services.GetRequiredService<ValueSerializerRegistry>());

    private RedisCache BuildCache()
    {
        _valueSerializers.ValidateWriteNames(_policyFactory);
        return new(
            _redis,
            _serializerProxy,
            _resiliencePipelineProvider,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _policyFactory,
            _clock,
            _loggerFactory.Create<RedisCache>(),
            _keyMaskingPolicy)
        {
            ValueSerializers = _valueSerializers,
        };
    }

    private RedisHashCache BuildHashCache()
    {
        _valueSerializers.ValidateWriteNames(_policyFactory);
        return new(
            _redis,
            _serializerProxy,
            _resiliencePipelineProvider,
            _cachingTelemetryProvider,
            _redisCacheOptions,
            _cacheOptions,
            _policyFactory,
            _clock,
            _loggerFactory.Create<RedisHashCache>(),
            _keyMaskingPolicy)
        {
            ValueSerializers = _valueSerializers,
        };
    }
}
