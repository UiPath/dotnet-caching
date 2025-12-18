using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisCache : RedisCacheBase, ICache
{
    private const string LogWarnMessage = "RedisCache exception.";

    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly ILogger<RedisCache> _logger;
    private readonly bool _supportsExpireTime;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly TimeSpan? _defaultExpiration;
    private readonly CacheClock _clock;
    private readonly Action<RedisKey, RedisValue>? _auditKeySize;
    private readonly int _largeValueThreshold;

    public RedisCache(
        IRedisConnector redis,
        ISerializerProxy<RedisValue> serializer,
        IResiliencePipelineHolder resiliencePipelineHolder,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ILogger<RedisCache> logger)
        : base(redis, telemetryProvider, redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled)
    {
        _logger = logger;
        _serializer = serializer;
        _read = resiliencePipelineHolder.Read;
        _write = resiliencePipelineHolder.Write;
        _supportsExpireTime = RedisUtils.SupportsExpireTime(redis.Version);
        _largeValueThreshold = cacheOptions.LargeValueThreshold;
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(cacheOptions, GetType());
        _defaultExpiration = redisCacheOptions.DefaultExpiration;
        _clock = new CacheClock(redisCacheOptions.Clock, _defaultExpiration);
        
        if (cacheOptions.AuditEnabled && _largeValueThreshold > 0)
        {
            _auditKeySize = AuditKeySize;
        }
    }

    public string Name => KnownCacheProviderNames.Redis;

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsync<T>(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsyncInternal<T>(cacheKeys, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _defaultExpiration, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToTimeSpan(expiration), token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        return GetOrAddInternalAsync(redisKey, generator, _clock.ToTimeSpan(expiration), token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _defaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        expiration = _clock.ToDateTimeOffset(expiration);

        _logger.LogTrace("Refreshing key {RedisKey} at expiration {Expiration}", redisKey, expiration);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            if (expiration == DateTimeOffset.MaxValue)
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            else
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyExpireAsync(redisKey, expiration.Value.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
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

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        SetAsync(cacheKey, value, _defaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetAsync(keyValues, _defaultExpiration, token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(keyValues, _clock.ToTimeSpan(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(keyValues, _clock.ToTimeSpan(expiration), token);
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
            _logger.LogWarning(ex, LogWarnMessage);
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
            _logger.LogWarning(ex, LogWarnMessage);
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
                ret = timeToLive.HasValue ? _clock.UtcNow.Add(timeToLive.Value) : null;
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
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
        var ret = await GetAsync<T>(redisKey, token).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {RedisKey}", redisKey);
        ret = await generator(token).ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await SetInternalAsync(redisKey, ret, expiration, token).ConfigureAwait(false);
        }

        return ret;
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
                await RemoveAsync<T>(redisKey, token).ConfigureAwait(false);
                ret = true;
            }
            else
            {

                var serialized = _serializer.Serialize(value);

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
            _logger.LogWarning(ex, LogWarnMessage);
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
                    _ = transaction.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster);
                }
                else
                {
                    var serialized = _serializer.Serialize(value);
                    _ = transaction.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster);
                }
            }

            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await transaction.ExecuteAsync().ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);

            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
        }
        finally
        {
            operation.Track(ret);
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
            _logger.LogWarning(ex, LogWarnMessage);
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
            _logger.LogWarning(ex, LogWarnMessage);
        }
        finally
        {
            operation.Track(ret);
        }

        return ret;
    }

    private async ValueTask<T?> GetAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        T? ret = default;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation<T>();
        
        if (!IsConnected)
        {
            return default;
        }

        try
        {
            var value = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.StringGetAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, RedisValue.Null, token).ConfigureAwait(false);
            _auditKeySize?.Invoke(redisKey, value);
            ret = value.IsNullOrEmpty ? default : _serializer.Deserialize<T>(value);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
        }
        finally
        {
            operation.Track(ret is not null);
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
        var redisKeys = keys.Select(keys => ToRedisKey(keys, token)).ToArray();
        var operation = StartOperation<T>();
        bool atLeastOneCacheHit = false;
        try
        {
            var values = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.StringGetAsync(redisKeys, CommandFlags.PreferReplica);
            }, [], token).ConfigureAwait(false);
            if (values.Length == 0)
            {
                retValues = GetDefaultValues<T>(keys);
            }
            else
            {
                for (int i = 0; i < redisKeys.Length; i++)
                {
                    var value = values[i];
                    _auditKeySize?.Invoke(redisKeys[i], value);
                    var obj = value.IsNullOrEmpty ? default : _serializer.Deserialize<T>(value);
                    atLeastOneCacheHit = atLeastOneCacheHit || obj is not null;
                    retValues[i] = new KeyValuePair<CacheKey, T?>(keys[i], obj);
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
            retValues = GetDefaultValues<T>(keys);
        }
        finally
        {
            operation.Track(atLeastOneCacheHit);
        }

        return retValues;
    }

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

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);

    private static KeyValuePair<CacheKey, T?>[] GetDefaultValues<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, T?>(k, default))];

    private void AuditKeySize(RedisKey key, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _largeValueThreshold)
        {
            _logger.LogWarning("Redis large value detected for key {RedisKey}, length {Length}", key, valueLen);
        }
    }
}
