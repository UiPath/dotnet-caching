using System.Runtime.CompilerServices;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : RedisCacheBase, ICache
{
    private readonly ISerializerProxy<byte[]> _serializer;
    private readonly IMemorySerializerProxy? _memorySerializer;
    private readonly ILogger<RedisCache> _logger;
    private readonly bool _supportsExpireTime;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly Action<RedisKey, RedisValue>? _auditKeySize;
    private readonly int _largeValueThreshold;
    private readonly bool _cacheNullValues;

    public RedisCache(
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger<RedisCache> logger)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory, clock)
    {
        _logger = logger;
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _read = resiliencePipelineProvider.Get(ResiliencePipelineNames.Read);
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _supportsExpireTime = RedisUtils.SupportsExpireTime(redis.Version);
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

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsync<T>(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsyncInternal<T>(cacheKeys, token);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetCacheEntryInternalAsync<T>(cacheKey, token);
    }

    public ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetCacheEntriesInternalAsync<T>(cacheKeys, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, PolicyDuration(policy), policy, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, CallerDuration(expiration), policy, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, CallerDuration(expiration), policy, token);

    private ValueTask<T?> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        var wrappedGenerator = WrapWithFactoryTimeout(generator, (policy ?? DefaultPolicy)?.FactoryTimeout, cacheKey);
        return GetOrAddInternalAsync(redisKey, wrappedGenerator, duration, token);
    }

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        var wrappedGenerator = WrapBatchWithFactoryTimeout<T, TState>(entries, generator, (policy ?? DefaultPolicy)?.FactoryTimeout);
        return BatchGetOrAdd.RunAsync<T, TState>(this, entries, wrappedGenerator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);
    }

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        var wrappedGenerator = WrapBatchWithFactoryTimeout<T, TState>(entries, generator, (policy ?? DefaultPolicy)?.FactoryTimeout);
        return BatchGetOrAdd.RunAsync<T, TState>(this, entries, wrappedGenerator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);
    }

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        var wrappedGenerator = WrapBatchWithFactoryTimeout<T, TState>(entries, generator, (policy ?? DefaultPolicy)?.FactoryTimeout);
        return BatchGetOrAdd.RunAsync<T, TState>(this, entries, wrappedGenerator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);
    }

    private Func<CancellationToken, Task<T?>> WrapWithFactoryTimeout<T>(Func<CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, CacheKey cacheKey)
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero)
        {
            return generator;
        }
        return token => FactoryTimeout.RunAsync(generator, factoryTimeout, cacheKey, Name, Telemetry, token);
    }

    private Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> WrapBatchWithFactoryTimeout<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        TimeSpan? factoryTimeout)
        where TState : notnull
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero || entries is not { Length: > 0 })
        {
            return generator;
        }
        return (states, token) => FactoryTimeout.RunAsync(
            t => generator(states, t),
            factoryTimeout,
            CompositeCacheKey.For(Array.ConvertAll(entries, e => e.Key)),
            Name,
            Telemetry,
            token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);

        LogRefreshingKey(redisKey, expiration);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            if (expiration == DateTimeOffset.MaxValue)
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyPersistAsync(redisKey, RefreshFlags).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            else
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyExpireAsync(redisKey, expiration.UtcDateTime, RefreshFlags).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
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
            operation.Track(ret);
        }

        return ret;
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        return RemoveAsync<T>(cacheKey.Select(k => ToRedisKey(k, token)).ToArray(), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, PolicyDuration(policy), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, PolicyDuration(policy), token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), token);

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(ToRedisKey(cacheKey, token), value, duration, token);
    }

    private ValueTask<bool> SetCoreAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan duration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(keyValues, duration, token);
    }

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, PolicyDuration(policy), token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), token);

    private ValueTask<bool> TryAddCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return TryAddInternalAsync(ToRedisKey(cacheKey, token), value, duration, token);
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
        var operation = StartOperation<T>();

        try
        {
            ret = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            operation.Track(ret);
        }

        return ret;
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        TimeSpan? ret = default;
        var operation = StartOperation<T>();
        try
        {
            ret = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyTimeToLiveAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            operation.Track(ret != null);
        }

        return ret;
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        DateTimeOffset? ret = default;
        var operation = StartOperation<T>();
        try
        {
            if (_supportsExpireTime)
            {
                ret = await _read.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyExpireTimeAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            else
            {
                var timeToLive = await TimeToLiveAsync<T>(cacheKey, token);
                ret = timeToLive.HasValue ? Clock.ToDateTimeOffset(timeToLive.Value) : null;
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
            operation.Track(ret.HasValue);
        }

        return ret;
    }

    private async ValueTask<T?> GetOrAddInternalAsync<T>(RedisKey redisKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var (found, cached) = await ReadGetOrAddProbeAsync<T>(redisKey, token).ConfigureAwait(false);
        if (found)
        {
            return cached;
        }

        LogCacheMissed(redisKey);
        var ret = await generator(token).ConfigureAwait(false);

        if (!IsDefault(ret) || _cacheNullValues)
        {
            await SetInternalAsync(redisKey, ret, expiration, token).ConfigureAwait(false);
        }

        return ret;
    }

    /// <summary>
    /// The value as the connection will send it. A serializer that can lend memory does, so a caller's buffer
    /// reaches the wire with no array in between; the memory is only borrowed, which is safe because every
    /// write awaits its command to completion and the connection copies the value as it writes the command.
    /// Any other serializer's array converts as it always has.
    /// </summary>
    private RedisValue SerializeValue<T>(T? value) =>
        _memorySerializer is { } memory
            ? (RedisValue)memory.SerializeToMemory(value)
            : (RedisValue)_serializer.Serialize(value);

    /// <summary>
    /// Tri-state read: <c>IsNull</c> -> miss; <c>Length == 0</c> -> cached-null hit (only honored when
    /// <c>CacheNullValues</c> is on); non-empty -> deserialize. Legacy round-trip to <c>default(T)</c>
    /// is also treated as a miss when the option is off.
    /// </summary>
    private (bool Found, T? Value) InterpretReadResult<T>(RedisValue value)
    {
        if (value.IsNull)
        {
            return (false, default);
        }
        var deserialized = value.Length() == 0 ? default : _serializer.Deserialize<T>(value);
        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return (false, default);
        }
        return (true, deserialized);
    }

    private async ValueTask<(bool Found, T? Value)> ReadGetOrAddProbeAsync<T>(RedisKey redisKey, CancellationToken token)
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
            var value = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.StringGetAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, RedisValue.Null, token).ConfigureAwait(false);
            _auditKeySize?.Invoke(redisKey, value);

            (found, deserialized) = InterpretReadResult<T>(value);
            operation.Stop();
        }
        catch (Exception ex)
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

    private async ValueTask<bool> SetInternalAsync<T>(RedisKey redisKey, T? value, TimeSpan expiration, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    ret = await _write.ExecuteAsync(async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return await Database.StringSetAsync(redisKey, RedisValue.EmptyString, expiration, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                    }, default, token).ConfigureAwait(false);
                }
                else
                {
                    await RemoveAsync<T>(redisKey, token).ConfigureAwait(false);
                    ret = true;
                }
            }
            else
            {

                var serialized = SerializeValue(value);

                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
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
            operation.Track(ret);
        }

        return ret;
    }

    /// <summary>
    /// <c>SET key value EX … NX</c> in one round-trip: Redis decides, so no probe precedes the write.
    /// Safe to retry — an attempt whose reply was lost is refused by the key it just wrote, and
    /// reports the same <c>false</c> the exception would have.
    /// </summary>
    private async ValueTask<bool> TryAddInternalAsync<T>(RedisKey redisKey, T? value, TimeSpan expiration, CancellationToken token)
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
                LogTryAddSkippedExpiredEntry(redisKey, expiration);
            }
            else if (isNull && !_cacheNullValues)
            {
                LogTryAddSkippedUnrepresentableValue(redisKey);
            }
            else
            {
                var serialized = isNull ? RedisValue.EmptyString : SerializeValue(value);

                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.StringSetAsync(redisKey, serialized, expiration, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
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
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> SetInternalAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        
        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            var transaction = Database.CreateTransaction(asyncState: null);

            foreach (var keyValue in keyValues)
            {
                var redisKey = ToRedisKey(keyValue.Key, token);
                var value = keyValue.Value;
                if (IsDefault(value))
                {
                    if (_cacheNullValues && expiration > TimeSpan.Zero)
                    {
                        _ = transaction.StringSetAsync(redisKey, RedisValue.EmptyString, expiration, When.Always, CommandFlags.DemandMaster);
                    }
                    else
                    {
                        _ = transaction.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster);
                    }
                }
                else
                {
                    var serialized = SerializeValue(value);
                    _ = transaction.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster);
                }
            }

            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);

            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            operation.Track(ret, keyValues.Length);
        }

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                return true;
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey[] redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation<T>();
        try
        {
            var response = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, -1, token).ConfigureAwait(false);
            operation.Stop();
            ret = response > -1;
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            operation.Track(ret, redisKey.Length);
        }

        return ret;
    }

    private async ValueTask<T?> GetAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        T? ret = default;
        bool found = false;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>();

        try
        {
            var value = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.StringGetAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, RedisValue.Null, token).ConfigureAwait(false);
            _auditKeySize?.Invoke(redisKey, value);
            (found, ret) = InterpretReadResult<T>(value);
            operation.Stop();
        }
        catch (Exception ex)
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

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsyncInternal<T>(CacheKey[] keys, CancellationToken token)
    {
        KeyValuePair<CacheKey, T?>[] retValues = new KeyValuePair<CacheKey, T?>[keys.Length];
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return retValues;
        }
        var redisKeys = keys.Select(k => ToRedisKey(k, token)).ToArray();
        if (!IsConnected)
        {
            return GetDefaultValues<T>(keys);
        }
        var operation = StartOperation<T>();
        var reads = InitReads(redisKeys);
        try
        {
            var values = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.StringGetAsync(redisKeys, CommandFlags.PreferReplica);
            }, [], token).ConfigureAwait(false);
            if (values.Length == redisKeys.Length)
            {
                for (int i = 0; i < redisKeys.Length; i++)
                {
                    var value = values[i];
                    _auditKeySize?.Invoke(redisKeys[i], value);
                    var (found, obj) = InterpretReadResult<T>(value);
                    reads[i].Hit = found;
                    retValues[i] = new KeyValuePair<CacheKey, T?>(keys[i], obj);
                }
            }
            else
            {
                retValues = GetDefaultValues<T>(keys);
            }
            operation.Stop();
        }
        catch (Exception ex)
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

        return retValues;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInternalAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return DefaultEntry<T>();
        }

        var operation = StartOperation<T>();
        ICacheEntry<T?> ret = DefaultEntry<T>();
        try
        {
            var transaction = Database.CreateTransaction();
            var valueTask = transaction.StringGetAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            ConfiguredTaskAwaitable<DateTime?>? expireTimeTask = default;
            ConfiguredTaskAwaitable<TimeSpan?>? ttlTask = default;
            if (_supportsExpireTime)
            {
                expireTimeTask = transaction.KeyExpireTimeAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }
            else
            {
                ttlTask = transaction.KeyTimeToLiveAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }

            var transactionResult = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await transaction.ExecuteAsync(CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);

            if (!transactionResult)
            {
                operation.Stop();
                return ret;
            }

            var value = await valueTask;
            _auditKeySize?.Invoke(redisKey, value);

            var (found, deserialized) = InterpretReadResult<T>(value);
            if (!found)
            {
                operation.Stop();
                return ret;
            }

            DateTimeOffset? expiration = _supportsExpireTime
                ? (DateTimeOffset?)await expireTimeTask!.Value
                : Clock.ToDateTimeOffset(await ttlTask!.Value);
            ret = _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expiration));
            operation.Stop();
        }
        catch (Exception ex)
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

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInternalAsync<T>(CacheKey[] keys, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return [];
        }
        var redisKeys = keys.Select(k => ToRedisKey(k, token)).ToArray();
        if (!IsConnected)
        {
            return GetDefaultEntries<T>(keys);
        }

        var operation = StartOperation<T>();
        var retValues = GetDefaultEntries<T>(keys);
        var reads = InitReads(redisKeys);
        try
        {
            var transaction = Database.CreateTransaction();
            var mgetTask = transaction.StringGetAsync(redisKeys, CommandFlags.PreferReplica).ConfigureAwait(false);
            var (expireTimeTasks, ttlTasks) = StartExpirationFetches(transaction, redisKeys);

            var transactionResult = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await transaction.ExecuteAsync(CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);

            if (!transactionResult)
            {
                operation.Stop();
                return retValues;
            }

            var values = await mgetTask;
            for (int i = 0; i < redisKeys.Length; i++)
            {
                var value = values[i];
                _auditKeySize?.Invoke(redisKeys[i], value);
                var (found, deserialized) = InterpretReadResult<T>(value);
                if (!found)
                {
                    continue;
                }
                DateTimeOffset? expiration = _supportsExpireTime
                    ? (DateTimeOffset?)await expireTimeTasks![i]
                    : Clock.ToDateTimeOffset(await ttlTasks![i]);
                reads[i].Hit = true;
                retValues[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                    keys[i],
                    _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expiration)));
            }
            operation.Stop();
        }
        catch (Exception ex)
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

        return retValues;
    }

    private (ConfiguredTaskAwaitable<DateTime?>[]? ExpireTimeTasks, ConfiguredTaskAwaitable<TimeSpan?>[]? TtlTasks)
        StartExpirationFetches(ITransaction transaction, RedisKey[] redisKeys)
    {
        if (_supportsExpireTime)
        {
            var expireTimeTasks = redisKeys
                .Select(k => transaction.KeyExpireTimeAsync(k, CommandFlags.PreferReplica).ConfigureAwait(false))
                .ToArray();
            return (expireTimeTasks, null);
        }
        var ttlTasks = redisKeys
            .Select(k => transaction.KeyTimeToLiveAsync(k, CommandFlags.PreferReplica).ConfigureAwait(false))
            .ToArray();
        return (null, ttlTasks);
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

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string methodName = "") =>
        Telemetry.StartOperation(Name, typeof(T), methodName);

    private static (RedisKey Key, bool Hit)[] InitReads(RedisKey[] redisKeys)
    {
        var reads = new (RedisKey Key, bool Hit)[redisKeys.Length];
        for (int i = 0; i < redisKeys.Length; i++)
        {
            reads[i] = (redisKeys[i], false);
        }
        return reads;
    }

    private void TrackPerKey(ITelemetryOperation operation, (RedisKey Key, bool Hit)[] reads)
    {
        var anyHit = false;
        foreach (var (_, hit) in reads)
        {
            anyHit |= hit;
        }
        operation.Track(anyHit, reads.Length);

        if (KeyReadTelemetryEnabled)
        {
            var keyed = new (string Key, bool Hit)[reads.Length];
            for (int i = 0; i < reads.Length; i++)
            {
                keyed[i] = (reads[i].Key.ToString(), reads[i].Hit);
            }
            operation.TrackKeyReads(keyed);
        }
    }

    private static KeyValuePair<CacheKey, T?>[] GetDefaultValues<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, T?>(k, default))];

    private void AuditKeySize(RedisKey key, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _largeValueThreshold)
        {
            LogLargeValueDetected(key, valueLen);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {Expiration}")]
    private partial void LogRefreshingKey(RedisKey redisKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: a default value cannot be represented unless CacheNullValues is on.")]
    private partial void LogTryAddSkippedUnrepresentableValue(RedisKey redisKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {RedisKey}: the requested expiration {Expiration} is not positive, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(RedisKey redisKey, TimeSpan expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisCache exception.")]
    private partial void LogRedisCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {RedisKey}")]
    private partial void LogCacheMissed(RedisKey redisKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, length {Length}")]
    private partial void LogLargeValueDetected(RedisKey redisKey, long length);
}
