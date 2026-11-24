using System.Collections.Immutable;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache : RedisCacheBase, IHashCache, ISpanKeyHashCache, ISharedHashTier
{
    /// <summary>All fields and the absolute expiration in one atomic read-only call.</summary>
    private const string ReadWithExpirationScript = "local n = redis.call('HLEN', KEYS[1]) if n > tonumber(ARGV[1]) then return {n, redis.call('PEXPIRETIME', KEYS[1])} end return {redis.call('HGETALL', KEYS[1]), redis.call('PEXPIRETIME', KEYS[1])}";

    /// <summary>Times <c>HashFieldBatchSize</c>: the field count above which a hash is read by pages.</summary>
    private const int PagedReadFactor = 10;
    private const int SerializerFailed = int.MaxValue;

    // Not Name, which both Redis caches share: the serializer metrics tell a hash from a string.
    private const string MetricCache = "RedisHash";

    private static readonly TimeSpan ChunkedWriteOrphanLifetime = TimeSpan.FromMinutes(10);

    private readonly ILogger<RedisHashCache> _logger;
    private readonly Action<Exception> _logFailure;
    private readonly Action<Exception> _logReadScriptUnavailable;
    private readonly Action<Exception> _rewriteScriptRefused;
    private readonly ISerializerProxy<byte[]> _serializer;
    private readonly IMemorySerializerProxy? _memorySerializer;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheOptions _cacheOptions;
    private readonly bool _auditLargeValues;
    private readonly bool _cacheNullValues;
    private readonly int _fieldBatchSize;
    private ValueSerializerRegistry _valueSerializers = ValueSerializerRegistry.Empty;
    private ValueFormatResolver _formats;
    private IEffectiveFormat? _effectiveFormat;

    public RedisHashCache(
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger<RedisHashCache> logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory, clock, keyMaskingPolicy)
    {
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _formats = new ValueFormatResolver(serializer, ValueSerializerRegistry.Empty, DefaultPolicy);
        _logger = logger;
        _logFailure = LogRedisHashCacheException;
        _logReadScriptUnavailable = LogReadScriptUnavailable;
        base.LogExpireTimeUnavailable = LogExpireTimeUnavailableMessage;
        _rewriteScriptRefused = RewriteScriptsRefused;
        _read = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Read));
        _write = new ConnectingPipeline(redis, resiliencePipelineProvider.Get(ResiliencePipelineNames.Write));
        _cacheOptions = cacheOptions;
        _cacheEntryFactory = redisCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _redisKeyStrategy = RedisKeyStrategies.Resolve(redisCacheOptions, _cacheOptions, GetType());
        _cacheNullValues = redisCacheOptions.CacheNullValues;
        _fieldBatchSize = Math.Max(1, redisCacheOptions.HashFieldBatchSize);
        _auditLargeValues = _cacheOptions.AuditEnabled && _cacheOptions.LargeValueThreshold > 0;
    }

    public string Name => KnownCacheProviderNames.Redis;

    internal ValueSerializerRegistry ValueSerializers
    {
        get => _valueSerializers;
        init
        {
            _valueSerializers = value;
            _formats = new ValueFormatResolver(_serializer, value, DefaultPolicy, _effectiveFormat);
        }
    }

    internal IEffectiveFormat? EffectiveFormat
    {
        get => _effectiveFormat;
        init
        {
            _effectiveFormat = value;
            _formats = new ValueFormatResolver(_serializer, _valueSerializers, DefaultPolicy, value);
        }
    }

    internal ValueMigration? Migration { get; init; }

    // For tests: runs before each chunk of a traced chunked merge, with the index of the chunk.
    internal Func<int, Task>? OnChunk { get; set; }

    // For tests: false makes a traced chunked merge take the watched-transaction fallback instead of the script.
    internal bool ChunkScriptsEnabled { get; set; } = true;

    // For tests: runs after a chunked replacement filled its staging key, before the rename.
    internal Func<IDatabase, RedisKey, Task>? OnStaged { get; set; }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetItemCoreAsync<T, ValueFormat>(cacheKey, field, format, token) : GetItemCoreAsync<T, PlainFormat>(cacheKey, field, default, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCoreAsync<T, ValueFormat>(cacheKey, format, token) : GetCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetFieldsCoreAsync<T, ValueFormat>(cacheKey, fields, format, token) : GetFieldsCoreAsync<T, PlainFormat>(cacheKey, fields, default, token);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntryCoreAsync<T, ValueFormat>(cacheKey, format, token) : GetCacheEntryCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> ReadForAdoptionAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff
            ? GetInnerCacheEntryAsync<T?, ValueFormat>(cacheKey, format, token, atomic: true)
            : GetInnerCacheEntryAsync<T?, PlainFormat>(cacheKey, default, token, atomic: true);
    }

    public ValueTask<bool> RemoveIfUnreadableAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff
            ? RemoveIfUnreadableCoreAsync<T, ValueFormat>(cacheKey, format, token)
            : RemoveIfUnreadableCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(policy), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<IDictionary<string, T?>>>>(cacheKey, generator, static (g, ct) => g(ct), GetExpiration(expiration), setOption ?? HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(policy), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync(cacheKey, state, generator, GetExpiration(expiration), setOption ?? HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        return RunAsync(_read, StartOperation<T>(), static (s, token) => s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask(), (Self: this, Key: redisKey), false, static hit => hit, _logFailure, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        RefreshWithOptionsAsync<T>(cacheKey, options, policy, FormatFor(policy, forWrite: true).Writer, token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        NoteWritten(redisKey);
        return NoteWrittenAfterAsync(redisKey, RunAsync(_write, StartOperation<T>(), static (s, token) => s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask(), (Self: this, Key: redisKey), false, static removed => removed, _logFailure, token));
    }

    public ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsCoreAsync<T>(cacheKey, fields, versioned: false, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        SetCountingAsync(cacheKey, values, options, policy, report: null, token);

    public async ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default)
    {
        var report = new WriteReport(limits);
        var written = await SetCountingAsync(cacheKey, values, options, policy, report, token).ConfigureAwait(false);
        return new VersionedWrite(written, report.Version, report.Fields);
    }

    public async ValueTask<VersionedWrite> FillVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default)
    {
        var report = new WriteReport(limits) { OnlyIfAbsent = true };
        var written = await SetCountingAsync(cacheKey, values, options, policy, report, token).ConfigureAwait(false);
        return new VersionedWrite(written, report.Version, report.Fields, report.Existed);
    }

    public ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsCoreAsync<T>(cacheKey, fields, versioned: true, token);

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyTimeToLiveAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyExpireTimeAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        // No policy is known here, and the metadata was written by whichever policy wrote the hash: every registered format reads.
        var format = FormatFor(policy: null, forWrite: false);
        return format.Sniff
            ? GetInnerAsync<IDictionary<string, string?>, ValueFormat>(cacheKey, KnownFieldNames.MetadataKey, format with { Readable = ReadableFormats.Any }, token)
            : GetInnerAsync<IDictionary<string, string?>, PlainFormat>(cacheKey, KnownFieldNames.MetadataKey, default, token);
    }

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataCoreAsync<T>(cacheKey, metadata, FormatFor(policy: null, forWrite: true).Writer, token);

    void ISharedTier.Invalidated(CacheKey key) => NoteInvalidated(_redisKeyStrategy, key);

    void ISharedTier.InvalidatedAll() => NoteAllInvalidated();

    IDisposable ISharedTier.DemandMaster() => DemandMaster();

    /// <summary>What an empty hash is stored as while nulls are cached: the metadata field, empty.</summary>
    private static HashEntry[] EmptyMarkerEntries() => [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)];

    private static ImmutableDictionary<string, T?> Empty<T>() => ImmutableDictionary<string, T?>.Empty;

    private TelemetryScope StartOperation<T>([CallerMemberName] string methodName = "") =>
        new(Telemetry, Clock, Name, methodName, typeof(T));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {LocalExpiration}")]
    private partial void LogRefreshingKey(LoggedKey redisKey, DateTimeOffset localExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripts are unavailable; hash cache reads now use an HGETALL and PTTL pair")]
    private partial void LogReadScriptUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EXPIRETIME is unavailable; hash cache expirations are now read from the TTL")]
    private partial void LogExpireTimeUnavailableMessage(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the script that rewrites hash fields; rewrites now use a transaction per field")]
    private partial void LogRewriteScriptRefused(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisHashCache exception.")]
    private partial void LogRedisHashCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A hash replacement expired before its rename; the key is deleted, as an expired write deletes it.")]
    private partial void LogExpiredReplacement(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A hash replacement past its deadline failed its rename for another reason than its expired staging key; the key is deleted, as an expired write deletes it.")]
    private partial void LogExpiredReplacementRenameFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A hash write landed without moving its version; the other nodes drop their copies of it.")]
    private partial void LogVersionNotCounted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis transaction failed.")]
    private partial void LogRedisTransactionFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, field {Field}, length {Length}")]
    private partial void LogLargeValueDetected(LoggedKey redisKey, string field, long length);
}
