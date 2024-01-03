using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisCache : RedisCacheBase, ICache
{
    private const string LogWarnMessage = "RedisCache exception.";

    private readonly IRedisConnector _redis;
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
        _redis = redis;
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

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetAsync<T>(ToRedisKey(cacheKey, token));
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
        return GetOrAddInternalAsync(redisKey, generator, _clock.ToTimeSpan(expiration));
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) where T : struct
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        return GetOrAddInternalAsync(redisKey, generator, _clock.ToTimeSpan(expiration));
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

        _logger.LogTrace("Refreshing key {redisKey} at expiration {expiration}", redisKey, expiration);
        var ret = false;
        var operation = StartOperation();
        try
        {
            ret = expiration == DateTimeOffset.MaxValue
                ? await _writePolicy.ExecuteAsync(() => Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false)
                : await _writePolicy.ExecuteAsync(() => Database.KeyExpireAsync(redisKey, expiration.Value.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false);
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
        return RemoveAsync(ToRedisKey(cacheKey, token));
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        SetAsync(cacheKey, value, _defaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration));
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration));
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
        var operation = StartOperation();

        try
        {
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
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
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyTimeToLiveAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
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
                ret = await _readPolicy.ExecuteAsync(() => Database.KeyExpireTimeAsync(ToRedisKey(cacheKey, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
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

    private async ValueTask<T?> GetOrAddInternalAsync<T>(RedisKey redisKey, Func<ValueTask<T?>> generator, TimeSpan expiration)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var ret = await GetAsync<T>(redisKey).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {}", redisKey);
        ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await SetInternalAsync(redisKey, ret, expiration).ConfigureAwait(false);
        }

        return ret;
    }

    private async ValueTask<bool> SetInternalAsync<T>(RedisKey redisKey, T? value, TimeSpan expiration)
    {
        bool ret = default;
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (IsDefault(value))
            {
                ret = await RemoveAsync(redisKey).ConfigureAwait(false);
            }
            else
            {

                var serialized = _serializer.Serialize(value);

                ret = await _writePolicy.ExecuteAsync(() => Database.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    private async ValueTask<bool> RemoveAsync(RedisKey redisKey)
    {
        bool ret = default;
        var operation = StartOperation();
        try
        {
            ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    private async ValueTask<T?> GetAsync<T>(RedisKey redisKey)
    {
        T? ret = default;
        var operation = StartOperation<T>();
        try
        {
            var value = await _readPolicy.ExecuteAsync(() => Database.StringGetAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
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
            operation.Track(ret != null);
        }

        return ret;
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
            _logger.LogWarning("Redis large value detected for key {redisKey}, length {length}", key, valueLen);
        }
    }
}
