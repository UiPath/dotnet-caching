using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : RedisCacheBase, ICache, IInvalidationSink
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
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly Action<LoggedKey, RedisValue>? _auditKeySize;
    private readonly int _largeValueThreshold;
    private readonly bool _cacheNullValues;
    private readonly ConcurrentDictionary<Type, object> _generations = new();
    private ValueSerializerRegistry _valueSerializers = ValueSerializerRegistry.Empty;
    private ValueFormatResolver _formats;

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
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _formats = new ValueFormatResolver(serializer, ValueSerializerRegistry.Empty, DefaultPolicy);
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
            _formats = new ValueFormatResolver(_serializer, value, DefaultPolicy);
        }
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        var redisKey = ToRedisKey(cacheKey, token);
        return format.Sniff ? GetAsync<T, ValueFormat>(redisKey, format, token) : GetAsync<T, PlainFormat>(redisKey, default, token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetAsyncInternal<T, ValueFormat>(cacheKeys, format, token) : GetAsyncInternal<T, PlainFormat>(cacheKeys, default, token);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntryInternalAsync<T, ValueFormat>(cacheKey, format, token) : GetCacheEntryInternalAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntriesInternalAsync<T, ValueFormat>(cacheKeys, format, token) : GetCacheEntriesInternalAsync<T, PlainFormat>(cacheKeys, default, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), PolicyDuration(policy), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), CallerDuration(expiration), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), CallerDuration(expiration), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, PolicyDuration(policy), policy, token);

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, CallerDuration(expiration), policy, token);

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, CallerDuration(expiration), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), generator, static (g, ct) => g(ct), policy, default(PlainFormat), token)
            : GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), generator, static (g, ct) => g(ct), policy, format, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), state, generator, policy, default(PlainFormat), token)
            : GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), state, generator, policy, format, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        return RemoveAsync<T>(Array.ConvertAll(cacheKey, k => ToRedisKey(k, token)), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, PolicyDuration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, PolicyDuration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, PolicyDuration(policy), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        return RunAsync(_read, StartOperation<T>(), static (s, token) => s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask(), (Self: this, Key: redisKey), false, static hit => hit, _logFailure, token);
    }

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

    void IInvalidationSink.Invalidated(CacheKey key) => NoteInvalidated(_redisKeyStrategy, key);

    void IInvalidationSink.InvalidatedAll() => NoteAllInvalidated();

    private static (RedisKey Key, bool Hit)[] InitReads(RedisKey[] redisKeys)
    {
        var reads = new (RedisKey Key, bool Hit)[redisKeys.Length];
        for (int i = 0; i < redisKeys.Length; i++)
        {
            reads[i] = (redisKeys[i], false);
        }
        return reads;
    }

    private static (RedisValue Value, DateTimeOffset? Expiration) ReadReply(RedisResult reply)
    {
        if (reply.IsNull)
        {
            return (RedisValue.Null, null);
        }

        var parts = ((RedisResult[]?)reply)!;
        var expireAt = (long)parts[1];
        // PEXPIRETIME answers -1 for a key without a TTL, as EXPIRETIME does for KeyExpireTimeAsync.
        return ((RedisValue)parts[0], expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt));
    }

    private static (RedisValue[] Values, DateTimeOffset?[] Expirations) ReadManyReply(RedisResult reply, int count)
    {
        var parts = ((RedisResult[]?)reply)!;
        var values = new RedisValue[count];
        var expirations = new DateTimeOffset?[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (RedisValue)parts[2 * i];
            var expireAt = (long)parts[(2 * i) + 1];
            expirations[i] = expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt);
        }

        return (values, expirations);
    }

    /// <summary>One attempt at a batch read: the script, or an MGET with a PTTL per key.</summary>
    private static async ValueTask<bool> ReadManyAsync((RedisCache Self, RedisKey[] Keys, StrongBox<StrongBox<(RedisValue[] Values, DateTimeOffset?[] Expirations)>?> Read) s, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await s.Self.TryReadScriptAsync(ReadManyWithExpirationScript, s.Keys, s.Self._logReadScriptUnavailable).ConfigureAwait(false) is { } reply)
        {
            Interlocked.CompareExchange(ref s.Read.Value, new(ReadManyReply(reply, s.Keys.Length)), null);
            return true;
        }

        // An MGET with a PTTL per key in one round trip; not atomic, see GetCacheEntryInternalAsync.
        var flags = s.Self.ReadFlagsFor(s.Keys);
        var mgetTask = s.Self.Database.StringGetAsync(s.Keys, flags);
        var expirationTasks = new Task<TimeSpan?>[s.Keys.Length];
        for (var i = 0; i < expirationTasks.Length; i++)
        {
            expirationTasks[i] = s.Self.Database.KeyTimeToLiveAsync(s.Keys[i], flags);
        }

        try
        {
            var values = await mgetTask.ConfigureAwait(false);
            var timesToLive = await Task.WhenAll(expirationTasks).ConfigureAwait(false);
            var clock = s.Self.Clock;
            Interlocked.CompareExchange(ref s.Read.Value, new((values, Array.ConvertAll(timesToLive, t => (DateTimeOffset?)clock.ToDateTimeOffset(t)))), null);
        }
        catch
        {
            mgetTask.Observe();
            Array.ForEach(expirationTasks, TaskObservation.Observe);
            throw;
        }

        return true;
    }

    private static KeyValuePair<CacheKey, T?>[] GetDefaultValues<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, T?>(k, default))];

    private ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchAsync<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddBatchAsync(entries, generator, DirectBatch<T, TState>.Invoke, setAsync, policy, token);
    }

    private ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchAsync<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        TArg argument,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        var wrappedGenerator = WrapBatchWithFactoryTimeout(entries, generator, (policy ?? DefaultPolicy)?.FactoryTimeout);
        return BatchGetOrAdd.RunAsync(this, entries, argument, wrappedGenerator, setAsync, policy, token);
    }

    private Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> WrapBatchWithFactoryTimeout<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        TimeSpan? factoryTimeout)
        where TState : notnull
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero || entries is not { Length: > 0 })
        {
            return generator;
        }
        return (argument, states, token) => FactoryTimeout.RunAsync(
            t => generator(argument, states, t),
            factoryTimeout,
            CompositeCacheKey.For(Array.ConvertAll(entries, e => e.Key)),
            Name,
            Telemetry,
            token);
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);

        LogRefreshingKey(Logged(cacheKey, redisKey, typeof(T)), expiration);
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            if (expiration == DateTimeOffset.MaxValue)
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyPersistAsync(s.Key, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireAsync(s.Key, s.Expiration.UtcDateTime, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey, Expiration: expiration),
                default,
                token).ConfigureAwait(false);
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true);
        return format.Writer is null
            ? SetInternalAsync(cacheKey, redisKey, value, duration, default(PlainFormat), token)
            : SetInternalAsync(cacheKey, redisKey, value, duration, format, token);
    }

    private ValueTask<bool> SetCoreAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: true);
        return format.Writer is null
            ? SetInternalAsync(keyValues, duration, default(PlainFormat), token)
            : SetInternalAsync(keyValues, duration, format, token);
    }

    private ValueTask<bool> TryAddCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true);
        return format.Writer is null
            ? TryAddInternalAsync(cacheKey, redisKey, value, duration, default(PlainFormat), token)
            : TryAddInternalAsync(cacheKey, redisKey, value, duration, format, token);
    }

    private ValueTask<T?> GetOrAddCoreAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? GetOrAddInternalAsync(cacheKey, redisKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, duration, default(PlainFormat), token)
            : GetOrAddInternalAsync(cacheKey, redisKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, duration, format, token);
    }

    private ValueTask<T?> GetOrAddInternalAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, TState State, Func<TState, CancellationToken, Task<T?>> Generator, TimeSpan? FactoryTimeout, TimeSpan Expiration, TFormat Format)>(
            redisKey,
            (this, cacheKey, redisKey, state, generator, factoryTimeout, expiration, format),
            static (args, run) => args.Cache.GenerateAndStoreAsync(args.Key, args.RedisKey, args.State, args.Generator, args.FactoryTimeout, args.Expiration, args.Format, run),
            token);
    }

    /// <summary>The work of a shared run, which ends the run itself (see <see cref="IInFlightEnd{TResult}"/>): out of the table with its result or failure before its task completes.</summary>
    private async ValueTask<T?> GenerateAndStoreAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, TimeSpan expiration, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        T? ret;
        try
        {
            var token = run.Token;
            // The one read of the run: every caller of the key shares it, a hit or a miss. Inlined rather than awaited
            // through ReadGetOrAddProbeAsync, so a hit runs one async frame fewer; a change to one goes in both.
            token.ThrowIfCancellationRequested();
            var found = false;
            T? cached = default;
            if (IsConnected)
            {
                var operation = StartOperation<T>(nameof(GetOrAddAsync));
                try
                {
                    var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
                    (found, cached, var unreadable) = InterpretReadResult<T, TFormat>(value, format);
                    if (unreadable != 0)
                    {
                        await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
                    }

                    operation.Stop();
                }
                catch (Exception ex) when (format.Handles(ex))
                {
                    operation.Stop();
                    LogRedisCacheException(ex);
                }
                finally
                {
                    TrackRead(operation, found, redisKey);
                }
            }

            if (found)
            {
                // The probe reads no TTL; a result call that joined this run reads it itself.
                run.ExpirationUnread = true;
                ret = cached;
            }
            else
            {
                LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
                ret = await RunGeneratorAsync(cacheKey, state, generator, factoryTimeout, token).ConfigureAwait(false);

                if ((!IsDefault(ret) || _cacheNullValues) && await SetInternalAsync(cacheKey, redisKey, ret, expiration, format, token).ConfigureAwait(false))
                {
                    run.Expiration = StoredDeadline(expiration);
                }
            }
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }

        run.Complete(ret);
        return ret;
    }

    /// <summary>One run per Redis key and type: concurrent callers join the run in progress, which reads Redis once and generates only on a miss, and share its result or failure; it runs on its own token, cancelled once every caller has cancelled. Keyed by the Redis key, not the cache key, which a strategy may map to different Redis keys, and by value, so a prefixed key is never rendered to a string. The work ends the run itself, so the caller that started it awaits the work's own task.</summary>
    private ValueTask<T?> ShareGenerationAsync<T, TArgs>(RedisKey redisKey, TArgs args, Func<TArgs, IInFlightEnd<T?>, ValueTask<T?>> work, CancellationToken token) =>
        GenerationsOf<T>().StartOrJoinAsync(new HashedRedisKey(redisKey), (Args: args, Work: work), static (state, run) => state.Work(state.Args, run), token, out _);

    /// <summary>As <see cref="ShareGenerationAsync{T, TArgs}(RedisKey, TArgs, Func{TArgs, IInFlightEnd{T}, ValueTask{T}}, CancellationToken)"/>, also handing back the run, whose <see cref="IInFlightRun.Expiration"/> is what the starter stored.</summary>
    private ValueTask<T?> ShareGenerationAsync<T, TArgs>(RedisKey redisKey, TArgs args, Func<TArgs, IInFlightEnd<T?>, ValueTask<T?>> work, CancellationToken token, out IInFlightRun flight) =>
        GenerationsOf<T>().StartOrJoinAsync(new HashedRedisKey(redisKey), (Args: args, Work: work), static (state, run) => state.Work(state.Args, run), token, out _, out flight);

    /// <summary>The runs of one value type, so a result needs no box and no cast.</summary>
    private InFlight<HashedRedisKey, T?> GenerationsOf<T>() =>
        (InFlight<HashedRedisKey, T?>)_generations.GetOrAdd(typeof(T), static _ => new InFlight<HashedRedisKey, T?>());

    private ValueTask<T?> GetOrAddGeneratedAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat =>
        ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, TState State, Func<TState, CancellationToken, Task<GeneratedValue<T>>> Generator, CachePolicy? Policy, TFormat Format)>(
            redisKey,
            (this, cacheKey, redisKey, state, generator, policy, format),
            static (args, run) => args.Cache.GenerateExpiringAndStoreAsync(args.Key, args.RedisKey, args.State, args.Generator, args.Policy, args.Format, run),
            token);

    /// <summary>As <see cref="GenerateAndStoreAsync{T, TState, TFormat}"/>, for a generator that returns the value's deadline; ends the run itself.</summary>
    private async ValueTask<T?> GenerateExpiringAndStoreAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        T? ret;
        try
        {
            var token = run.Token;
            var (found, cached) = await ReadGetOrAddProbeAsync<T, TFormat>(redisKey, format, token).ConfigureAwait(false);
            if (found)
            {
                // The probe reads no TTL; a result call that joined this run reads it itself.
                run.ExpirationUnread = true;
                ret = cached;
            }
            else
            {
                LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
                var generated = await RunGeneratorAsync(cacheKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, token).ConfigureAwait(false);

                var duration = generated.Expiration is { } expiration ? Remaining(expiration) : PolicyDuration(policy);
                if (duration is { } lifetime && (!IsDefault(generated.Value) || _cacheNullValues)
                    && await SetInternalAsync(cacheKey, redisKey, generated.Value, lifetime, format, token).ConfigureAwait(false))
                {
                    run.Expiration = StoredDeadline(lifetime);
                }

                ret = generated.Value;
            }
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }

        run.Complete(ret);
        return ret;
    }

    private TimeSpan? Remaining(DateTimeOffset expiration)
    {
        if (expiration == DateTimeOffset.MaxValue)
        {
            return TimeSpan.MaxValue;
        }

        var remaining = expiration - Clock.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private Task<TResult> RunGeneratorAsync<TResult, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<TResult>> generator, TimeSpan? factoryTimeout, CancellationToken token) =>
        factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero
            ? generator(state, token)
            : FactoryTimeout.RunAsync(ct => generator(state, ct), factoryTimeout, cacheKey, Name, Telemetry, token);

    /// <summary>Borrowed memory is safe here because every write awaits its command and the connection copies the value while writing it; pooled memory goes back only then.</summary>
    private (RedisValue Value, SerializedPayload Payload) SerializeValue<T, TFormat>(T? value, TFormat format)
        where TFormat : struct, IValueFormat
    {
        if (format.Writer is { } writer)
        {
            var headered = SerializedPayload.Serialize(writer, _valueSerializers.FormatIdsOf(writer).Span, value!);
            return (headered.Memory, headered);
        }

        if (_memorySerializer is { } memory)
        {
            var payload = SerializedPayload.Serialize(memory, value);
            return (payload.Memory, payload);
        }
        return (_serializer.Serialize(value), default);
    }

    /// <summary>
    /// Tri-state read: <c>IsNull</c> -> miss; <c>Length == 0</c> -> cached-null hit (only honored when
    /// <c>CacheNullValues</c> is on); non-empty -> deserialize. Legacy round-trip to <c>default(T)</c>
    /// is also treated as a miss when the option is off.
    /// </summary>
    private (bool Found, T? Value, int Unreadable) InterpretReadResult<T, TFormat>(RedisValue value, TFormat format)
        where TFormat : struct, IValueFormat
    {
        if (value.IsNull)
        {
            return (false, default, 0);
        }

        T? deserialized;
        if (value.Length() == 0)
        {
            deserialized = default;
        }
        else if (format.Sniff)
        {
            var outcome = _formats.Reader!.Read<T>(((byte[]?)value)!);
            if (outcome.IsMiss)
            {
                if (outcome.Failure is { } failure)
                {
                    LogRedisCacheException(failure);
                }

                return (false, default, outcome.UnreadableFormat);
            }

            deserialized = outcome.Value;
        }
        else
        {
            deserialized = _serializer.Deserialize<T>(value);
        }

        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return (false, default, 0);
        }
        return (true, deserialized, 0);
    }

    /// <summary>As <see cref="InterpretReadResult{T, TFormat}"/>, for one value of a batch: a format no serializer owns joins <paramref name="unreadableKeys"/>, which the batch drops after it has reported.</summary>
    private (bool Found, T? Value) InterpretBatchRead<T, TFormat>(RedisKey redisKey, RedisValue value, TFormat format, ref List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys)
        where TFormat : struct, IValueFormat
    {
        var (found, deserialized, unreadable) = InterpretReadResult<T, TFormat>(value, format);
        if (unreadable != 0)
        {
            (unreadableKeys ??= []).Add((redisKey, value, unreadable));
        }

        return (found, deserialized);
    }

    private ValueTask<RedisValue> ReadStringAsync(RedisKey redisKey, CancellationToken token) =>
        _read.ExecuteAsync(static (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            return s.Self.Database.StringGetAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
        },
        (Self: this, Key: redisKey),
        RedisValue.Null,
        token);

    // GenerateAndStoreAsync carries an inlined copy of this read; a change to one goes in both.
    private async ValueTask<(bool Found, T? Value)> ReadGetOrAddProbeAsync<T, TFormat>(RedisKey redisKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return (false, default);
        }

        var operation = StartOperation<T>(nameof(GetOrAddAsync));
        bool found = false;
        T? deserialized = default;
        try
        {
            var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
            (found, deserialized, var unreadable) = InterpretReadResult<T, TFormat>(value, format);
            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return (found, deserialized);
    }

    private async ValueTask<bool> SetInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return false;
        }

        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.Self.Database.StringSetAsync(s.Key, RedisValue.EmptyString, s.Expiration, When.Always, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Expiration: expiration),
                    default,
                    token).ConfigureAwait(false);
                }
                else
                {
                    await RemoveAsync<T>(redisKey, token).ConfigureAwait(false);
                    ret = true;
                }
            }
            else
            {
                var (serialized, payload) = SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
                using (payload)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.Self.Database.StringSetAsync(s.Key, s.Serialized, s.Expiration, When.Always, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Serialized: serialized, Expiration: expiration),
                    default,
                    token).ConfigureAwait(false);
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> SetInternalAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var redisKeys = Array.ConvertAll(keyValues, kv => ToRedisKey(kv.Key, token));
        NoteWritten(redisKeys);

        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(SetAsync));
        var payloads = new SerializedPayload[keyValues.Length];
        try
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            var committed = await PerSlotAsync(
                SlotGroups(redisKeys, _logger),
                (Self: this, KeyValues: keyValues, Keys: redisKeys, Payloads: payloads, Expiration: expiration, Format: format, Token: token),
                static (s, group) => s.Self.SetSlotAsync(s.KeyValues, s.Keys, s.Payloads, group, s.Expiration, s.Format, s.Token),
                false,
                LogRedisCacheException).ConfigureAwait(false);
            ret = Array.TrueForAll(committed, static c => c);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            foreach (var payload in payloads)
            {
                payload.Dispose();
            }
            NoteWritten(redisKeys);
            operation.Track(ret, keyValues.Length);
        }

        return ret;
    }

    /// <summary>One transaction for the keys of one slot; it serializes into <paramref name="payloads"/>, which the caller disposes.</summary>
    private ValueTask<bool> SetSlotAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] keyValues, RedisKey[] redisKeys, SerializedPayload[] payloads, int[] group, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var transaction = Database.CreateTransaction(asyncState: null);
        foreach (var i in group)
        {
            var redisKey = redisKeys[i];
            var value = keyValues[i].Value;
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    transaction.StringSetAsync(redisKey, RedisValue.EmptyString, expiration, When.Always, CommandFlags.DemandMaster).Forget();
                }
                else
                {
                    transaction.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).Forget();
                }
            }
            else
            {
                (var serialized, payloads[i]) = SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(keyValues[i].Key, redisKey, typeof(T)), serialized);
                transaction.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster).Forget();
            }
        }

        return _write.ExecuteAsync(static (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
        },
        transaction,
        default,
        token);
    }

    /// <summary>
    /// <c>SET key value EX … NX</c> in one round-trip: Redis decides, so no probe precedes the write.
    /// Safe to retry — an attempt whose reply was lost is refused by the key it just wrote, and
    /// reports the same <c>false</c> the exception would have.
    /// </summary>
    private async ValueTask<bool> TryAddInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(TryAddAsync));
        try
        {
            var isNull = IsDefault(value);
            if (expiration <= TimeSpan.Zero)
            {
                LogTryAddSkippedExpiredEntry(Logged(cacheKey, redisKey, typeof(T)), expiration);
            }
            else if (isNull && !_cacheNullValues)
            {
                LogTryAddSkippedUnrepresentableValue(Logged(cacheKey, redisKey, typeof(T)));
            }
            else
            {
                NoteWritten(redisKey);
                var (serialized, payload) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
                using (payload)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.Self.Database.StringSetAsync(s.Key, s.Serialized, s.Expiration, When.NotExists, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Serialized: serialized, Expiration: expiration),
                    default,
                    token).ConfigureAwait(false);
                }
            }
            operation.Stop();
        }
        // false would claim someone else owns the key, which a cancelled call never established.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Stop();
            throw;
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey[] redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            var responses = await PerSlotAsync(
                SlotGroups(redisKey, _logger),
                (Self: this, Keys: redisKey, Token: token),
                static (s, group) => s.Self._write.ExecuteAsync(static (r, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return r.Self.Database.KeyDeleteAsync(r.Keys, CommandFlags.DemandMaster).AsValueTask();
                },
                (s.Self, Keys: Pick(s.Keys, group)),
                -1L,
                s.Token),
                -1L,
                LogRedisCacheException).ConfigureAwait(false);
            operation.Stop();
            ret = Array.TrueForAll(responses, static r => r > -1);
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret, redisKey.Length);
        }

        return ret;
    }

    private async ValueTask<T?> GetAsync<T, TFormat>(RedisKey redisKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        T? ret = default;
        bool found = false;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>(nameof(GetAsync));

        try
        {
            var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
            (found, ret, var unreadable) = InterpretReadResult<T, TFormat>(value, format);
            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsyncInternal<T, TFormat>(CacheKey[] keys, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        KeyValuePair<CacheKey, T?>[] retValues = new KeyValuePair<CacheKey, T?>[keys.Length];
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return retValues;
        }
        var redisKeys = Array.ConvertAll(keys, k => ToRedisKey(k, token));
        if (!IsConnected)
        {
            return GetDefaultValues<T>(keys);
        }
        var operation = StartOperation<T>(nameof(GetAsyncInternal));
        var reads = InitReads(redisKeys);
        List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys = null;
        try
        {
            await EnsureConnectedAsync(_read, token).ConfigureAwait(false);
            var groups = SlotGroups(redisKeys, _logger);
            var groupValues = await PerSlotAsync(
                groups,
                (Self: this, Keys: redisKeys, Token: token),
                static (s, group) => s.Self._read.ExecuteAsync(static (r, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return r.Self.Database.StringGetAsync(r.Keys, r.Self.ReadFlagsFor(r.Keys)).AsValueTask();
                },
                (s.Self, Keys: Pick(s.Keys, group)),
                [],
                s.Token),
                [],
                LogRedisCacheException).ConfigureAwait(false);
            retValues = GetDefaultValues<T>(keys);
            for (var g = 0; g < groups.Length; g++)
            {
                var (group, values) = (groups[g], groupValues[g]);
                // A slot whose read answered nothing reads as misses; the others keep their values.
                if (values.Length != group.Length)
                {
                    continue;
                }

                for (var j = 0; j < group.Length; j++)
                {
                    var i = group[j];
                    var (found, obj) = InterpretBatchRead<T, TFormat>(redisKeys[i], values[j], format, ref unreadableKeys);
                    reads[i].Hit = found;
                    retValues[i] = new KeyValuePair<CacheKey, T?>(keys[i], obj);
                }
            }
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
            retValues = GetDefaultValues<T>(keys);
            reads = InitReads(redisKeys);
        }
        finally
        {
            TrackPerKey(operation, reads);
        }

        if (unreadableKeys is not null)
        {
            await RemoveUnreadableAsync<T, TFormat>(unreadableKeys, format, token).ConfigureAwait(false);
        }

        return retValues;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInternalAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token, string methodName = nameof(GetCacheEntryInternalAsync))
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return DefaultEntry<T>();
        }

        var operation = StartOperation<T>(methodName);
        ICacheEntry<T?> ret = DefaultEntry<T>();
        try
        {
            // Per attempt; boxed so the pipeline stays bool.
            var read = new StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?>();
            var committed = await _read.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (await s.Self.TryReadScriptAsync(ReadWithExpirationScript, [s.Key], s.Self._logReadScriptUnavailable).ConfigureAwait(false) is { } reply)
                {
                    Interlocked.CompareExchange(ref s.Read.Value, new(ReadReply(reply)), null);
                    return true;
                }

                // A GET with a PTTL in one round trip. Not atomic: a key that expires between them reads as a value without a TTL.
                var flags = s.Self.ReadFlagsFor(s.Key);
                var valueTask = s.Self.Database.StringGetAsync(s.Key, flags);
                var timeToLiveTask = s.Self.Database.KeyTimeToLiveAsync(s.Key, flags);
                try
                {
                    var value = await valueTask.ConfigureAwait(false);
                    var expiration = s.Self.Clock.ToDateTimeOffset(await timeToLiveTask.ConfigureAwait(false));
                    Interlocked.CompareExchange(ref s.Read.Value, new((value, expiration)), null);
                }
                catch
                {
                    valueTask.Observe();
                    timeToLiveTask.Observe();
                    throw;
                }

                return true;
            },
            (Self: this, Key: redisKey, Read: read),
            default,
            token).ConfigureAwait(false);

            if (!committed || read.Value is not { } result)
            {
                operation.Stop();
                return ret;
            }

            var (value, expiration) = result.Value;
            var (found, deserialized, unreadable) = InterpretReadResult<T, TFormat>(value, format);
            if (found)
            {
                ret = _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expiration));
            }

            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInternalAsync<T, TFormat>(CacheKey[] keys, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return [];
        }
        var redisKeys = Array.ConvertAll(keys, k => ToRedisKey(k, token));
        if (!IsConnected)
        {
            return GetDefaultEntries<T>(keys);
        }

        var operation = StartOperation<T>(nameof(GetCacheEntriesInternalAsync));
        var retValues = GetDefaultEntries<T>(keys);
        var reads = InitReads(redisKeys);
        List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys = null;
        try
        {
            await EnsureConnectedAsync(_read, token).ConfigureAwait(false);
            var groups = SlotGroups(redisKeys, _logger);
            var groupReads = await PerSlotAsync(
                groups,
                (Self: this, Keys: redisKeys, Token: token),
                static async (s, group) =>
                {
                    var read = new StrongBox<StrongBox<(RedisValue[] Values, DateTimeOffset?[] Expirations)>?>();
                    var committed = await s.Self._read.ExecuteAsync(
                    ReadManyAsync,
                    (s.Self, Keys: Pick(s.Keys, group), Read: read),
                    default,
                    s.Token).ConfigureAwait(false);
                    return committed ? read.Value : null;
                },
                null,
                LogRedisCacheException).ConfigureAwait(false);

            for (var g = 0; g < groups.Length; g++)
            {
                // A slot whose read did not commit reads as misses; the others keep their entries.
                if (groupReads[g] is not { } result)
                {
                    continue;
                }

                var group = groups[g];
                var (values, expirations) = result.Value;
                for (var j = 0; j < group.Length; j++)
                {
                    var i = group[j];
                    var (found, deserialized) = InterpretBatchRead<T, TFormat>(redisKeys[i], values[j], format, ref unreadableKeys);
                    if (!found)
                    {
                        continue;
                    }
                    reads[i].Hit = true;
                    retValues[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                        keys[i],
                        _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expirations[j])));
                }
            }
            operation.Stop();
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
            retValues = GetDefaultEntries<T>(keys);
            reads = InitReads(redisKeys);
        }
        finally
        {
            TrackPerKey(operation, reads);
        }

        if (unreadableKeys is not null)
        {
            await RemoveUnreadableAsync<T, TFormat>(unreadableKeys, format, token).ConfigureAwait(false);
        }

        return retValues;
    }

    private ICacheEntry<T?> DefaultEntry<T>() =>
        _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);

    private KeyValuePair<CacheKey, ICacheEntry<T?>>[] GetDefaultEntries<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, ICacheEntry<T?>>(k, DefaultEntry<T>()))];

    private RedisKey ToRedisKey(CacheKey cacheKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }

        return _redisKeyStrategy.GetRedisKey(cacheKey);
    }

    private TelemetryScope StartOperation<T>([CallerMemberName] string methodName = "") =>
        new(Telemetry, Clock, Name, methodName, typeof(T));

    private void TrackPerKey(TelemetryScope operation, (RedisKey Key, bool Hit)[] reads)
    {
        var anyHit = false;
        foreach (var (_, hit) in reads)
        {
            anyHit |= hit;
        }
        operation.Track(anyHit, reads.Length);

        if (KeyReadTelemetryEnabled && operation.IsEnabled)
        {
            var keyed = new (string Key, bool Hit)[reads.Length];
            for (int i = 0; i < reads.Length; i++)
            {
                keyed[i] = (reads[i].Key.ToString(), reads[i].Hit);
            }
            operation.TrackKeyReads(keyed);
        }
    }

    private void AuditKeySize(LoggedKey key, RedisValue value)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        var valueLen = value.Length();
        if (valueLen > _largeValueThreshold)
        {
            LogLargeValueDetected(key, valueLen);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {Expiration}")]
    private partial void LogRefreshingKey(LoggedKey redisKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: a default value cannot be represented unless CacheNullValues is on.")]
    private partial void LogTryAddSkippedUnrepresentableValue(LoggedKey redisKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: the requested expiration {Expiration} is not positive, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(LoggedKey redisKey, TimeSpan expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripts are unavailable; cache reads now use a GET and PTTL pair")]
    private partial void LogReadScriptUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisCache exception.")]
    private partial void LogRedisCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {RedisKey}")]
    private partial void LogCacheMissed(LoggedKey redisKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, length {Length}")]
    private partial void LogLargeValueDetected(LoggedKey redisKey, long length);
}
