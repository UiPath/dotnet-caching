using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisCache : RedisCacheBase, ICache
{
    private const string LogWarnMessage = "RedisCache exception.";

    private readonly ISerializerProxy _serializer;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ILogger<RedisCache> _logger;
    private readonly bool _supportsExpireTime;
    private readonly IPolicyExecutor _readPolicy;
    private readonly IPolicyExecutor _writePolicy;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly TimeSpan? _defaultExpiration;
    private readonly CacheClock _clock;
    private readonly Action<RedisKey, RedisValue>? _auditKeySize;
    private readonly int _largeValueThreshold;

    public RedisCache(
        IRedisConnector redis,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ILogger<RedisCache> logger)
        : base(redis, redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled)
    {
        _logger = logger;
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
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

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsyncInternal<T>(cacheKey, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _defaultExpiration, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToTimeSpan(expiration), token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        return GetOrAddInternalAsync(redisKey, generator, _clock.ToTimeSpan(expiration), token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) where T : struct
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
        var operation = StartOperation();
        try
        {
            ret = expiration == DateTimeOffset.MaxValue
                ? await _writePolicy.ExecuteAsync(() => Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget), token).ConfigureAwait(false)
                : await _writePolicy.ExecuteAsync(() => Database.KeyExpireAsync(redisKey, expiration.Value.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget), token).ConfigureAwait(false);
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
        return RemoveAsync(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        return RemoveAsync(cacheKey.Select(k => ToRedisKey(k, token)).ToArray(), token);
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
        var operation = StartOperation();

        try
        {
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica), token).ConfigureAwait(false);
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
        var operation = StartOperation();
        try
        {
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyTimeToLiveAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica), token).ConfigureAwait(false);
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
        var operation = StartOperation();
        try
        {
            if (_supportsExpireTime)
            {
                ret = await _readPolicy.ExecuteAsync(() => Database.KeyExpireTimeAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica), token).ConfigureAwait(false);
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

    private async ValueTask<T?> GetOrAddInternalAsync<T>(RedisKey redisKey, Func<ValueTask<T?>> generator, TimeSpan expiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var ret = await GetAsync<T>(redisKey, token).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {RedisKey}", redisKey);
        ret = await generator().ConfigureAwait(false);

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
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (IsDefault(value))
            {
                ret = await RemoveAsync(redisKey, token).ConfigureAwait(false);
            }
            else
            {

                var serialized = _serializer.Serialize(value);

                ret = await _writePolicy.ExecuteAsync(() => Database.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster), token).ConfigureAwait(false);
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
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            var transaction = await _writePolicy.ExecuteAsync(() => Task.Factory.StartNew(() => Database.CreateTransaction(asyncState: null)), token);

            foreach (var keyValue in keyValues)
            {
                var redisKey = ToRedisKey(keyValue.Key, token);
                var value = keyValue.Value;
                if (IsDefault(value))
                {
                    _ = _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster), token);
                }
                else
                {
                    var serialized = _serializer.Serialize(value);
                    _ = _writePolicy.ExecuteAsync(() => Database.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.FireAndForget), token);
                }
            }

            ret = await _writePolicy.ExecuteAsync(() => transaction.ExecuteAsync(), token);
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

    private async ValueTask<bool> RemoveAsync(RedisKey redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation();
        try
        {
            ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster), token).ConfigureAwait(false);
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

    private async ValueTask<bool> RemoveAsync(RedisKey[] redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation();
        try
        {
            await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster), token).ConfigureAwait(false);
            operation.Stop();
            ret = true;
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
        try
        {
            var value = await _readPolicy.ExecuteAsync(() => Database.StringGetAsync(redisKey, CommandFlags.PreferReplica), token).ConfigureAwait(false);
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
        var redisKeys = keys.Select(keys => ToRedisKey(keys, token)).ToArray();
        var operation = StartOperation<T>();
        try
        {
            var values = await _readPolicy.ExecuteAsync(() => Database.StringGetAsync(redisKeys, CommandFlags.PreferReplica), token).ConfigureAwait(false);
            for (int i = 0; i < redisKeys.Length; i++)
            {
                var value = values[i];
                _auditKeySize?.Invoke(redisKeys[i], value);
                var obj = value.IsNullOrEmpty ? default : _serializer.Deserialize<T>(value);
                retValues[i] = new KeyValuePair<CacheKey, T?>(keys[i], obj);
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
            //operation.Track(ret != null);
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

    private ITelemetryOperation StartOperation([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisCache>(name);

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisCache, T>(name);

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);

    private void AuditKeySize(RedisKey key, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _largeValueThreshold)
        {
            _logger.LogWarning("Redis large value detected for key {RedisKey}, length {Length}", key, valueLen);
        }
    }
}
