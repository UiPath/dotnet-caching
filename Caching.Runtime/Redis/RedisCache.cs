using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisCache : ICache
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

    public RedisCache(
        IRedisConnector redis,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<RedisCacheOptions> redisCacheOptionsAccessor,
        IOptions<CacheOptions> optionsAccessor,
        ILogger<RedisCache> logger)
    {   
        _logger = logger;
        _redis = redis;
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
        var redisCacheOptions = redisCacheOptionsAccessor.Value;
        _supportsExpireTime = RedisUtils.SupportsExpireTime(redisCacheOptions.Version);
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(optionsAccessor.Value, GetType());
        _defaultExpiration = redisCacheOptions.DefaultExpiration;
        _clock = new CacheClock(redisCacheOptions.Clock, _defaultExpiration);
    }

    private IDatabase Database => _redis.Database;

    public Task<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        GetAsync<T?>(ToRedisKey(cacheKey, token));

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _defaultExpiration, token);

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToTimeSpan(expiration), token);

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);

        if (generator == null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        return GetOrAddInternalAsync(redisKey, generator, _clock.ToTimeSpan(expiration));
    }

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _defaultExpiration, token);

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async Task<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
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

    public Task<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RemoveAsync(ToRedisKey(cacheKey, token));

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        SetAsync(cacheKey, value, _defaultExpiration, token);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration));

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        SetInternalAsync(ToRedisKey(cacheKey, token), value, _clock.ToTimeSpan(expiration));

    public async Task<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
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

    public async Task<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
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

    public async Task<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
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

    private async Task<T?> GetOrAddInternalAsync<T>(RedisKey redisKey, Func<Task<T?>> generator, TimeSpan expiration)
    {
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

    private async Task<bool> SetInternalAsync<T>(RedisKey redisKey, T? value, TimeSpan expiration)
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

    private async Task<bool> RemoveAsync(RedisKey redisKey)
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

    private async Task<T?> GetAsync<T>(RedisKey redisKey)
    {
        T? ret = default;
        var operation = StartOperation<T>();
        try
        {
            var value = await _readPolicy.ExecuteAsync(() => Database.StringGetAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
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

    public void Dispose()
    {
        // nothing to dispose
    }
}
