using System.Collections.Concurrent;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : RedisCacheBase, ICache, ISharedTier, IMigrationCache
{
    /// <summary>Value and absolute expiration in one atomic read-only call.</summary>
    private const string ReadWithExpirationScript = "local v = redis.call('GET', KEYS[1]) if not v then return false end return {v, redis.call('PEXPIRETIME', KEYS[1])}";

    /// <summary>Each key's value and absolute expiration, flat: a missing key answers a null value and -2.</summary>
    private const string ReadManyWithExpirationScript = "local r = {} for i, k in ipairs(KEYS) do r[2 * i - 1] = redis.call('GET', k) r[2 * i] = redis.call('PEXPIRETIME', k) end return r";

    private readonly ISerializerProxy<byte[]> _serializer;

    private readonly IMemorySerializerProxy? _memorySerializer;

    private readonly ILogger<RedisCache> _logger;

    private readonly Action<Exception> _logFailure;

    private readonly Action<Exception> _logReadScriptUnavailable;

    private readonly Action<Exception> _rewriteScriptRefused;

    private readonly IResiliencePipeline _read;

    private readonly IResiliencePipeline _write;

    private readonly IRedisKeyStrategy _redisKeyStrategy;

    private readonly ICacheEntryFactory _cacheEntryFactory;

    private readonly Action<LoggedKey, RedisValue>? _auditKeySize;

    private readonly int _largeValueThreshold;

    private readonly bool _cacheNullValues;

    private readonly bool _removeUnreadable;

    private readonly ConcurrentDictionary<Type, object> _generations = new();

    private ValueSerializerRegistry _valueSerializers = ValueSerializerRegistry.Empty;

    private ValueFormatResolver _formats;

    private IEffectiveFormat? _effectiveFormat;

    public RedisCache(
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger<RedisCache> logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory, clock, keyMaskingPolicy)
    {
        _logger = logger;
        _logFailure = LogRedisCacheException;
        _logReadScriptUnavailable = LogReadScriptUnavailable;
        _rewriteScriptRefused = RewriteScriptsRefused;
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _removeUnreadable = redisCacheOptions.RemoveUnreadable;
        _formats = new ValueFormatResolver(serializer, ValueSerializerRegistry.Empty, DefaultPolicy, classifyHeadersWithNoRegistry: !_removeUnreadable);
        _read = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Read));
        _write = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Write));
        _largeValueThreshold = cacheOptions.LargeValueThreshold;
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(cacheOptions, GetType());
        _cacheEntryFactory = redisCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _cacheNullValues = redisCacheOptions.CacheNullValues;

        if (cacheOptions.AuditEnabled && _largeValueThreshold > 0)
        {
            _auditKeySize = AuditKeySize;
        }
    }

    public string Name => KnownCacheProviderNames.Redis;

    internal ValueSerializerRegistry ValueSerializers
    {
        get => _valueSerializers;
        init
        {
            _valueSerializers = value;
            _formats = new ValueFormatResolver(_serializer, value, DefaultPolicy, _effectiveFormat, !_removeUnreadable);
        }
    }

    internal IEffectiveFormat? EffectiveFormat
    {
        get => _effectiveFormat;
        init
        {
            _effectiveFormat = value;
            _formats = new ValueFormatResolver(_serializer, _valueSerializers, DefaultPolicy, value, !_removeUnreadable);
        }
    }

    internal ValueMigration? Migration { get; init; }

    void ISharedTier.Invalidated(CacheKey key) => NoteInvalidated(_redisKeyStrategy, key);

    void ISharedTier.InvalidatedAll() => NoteAllInvalidated();

    IDisposable ISharedTier.DemandMaster() => DemandMaster();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {Expiration}")]
    private partial void LogRefreshingKey(LoggedKey redisKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: a default value cannot be represented unless CacheNullValues is on.")]
    private partial void LogTryAddSkippedUnrepresentableValue(LoggedKey redisKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: the requested expiration {Expiration} is not positive, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(LoggedKey redisKey, TimeSpan expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripts are unavailable; cache reads now use a GET and PTTL pair")]
    private partial void LogReadScriptUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the script that rewrites a value; rewrites now use a transaction")]
    private partial void LogRewriteScriptRefused(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisCache exception.")]
    private partial void LogRedisCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {RedisKey}")]
    private partial void LogCacheMissed(LoggedKey redisKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, length {Length}")]
    private partial void LogLargeValueDetected(LoggedKey redisKey, long length);
}
