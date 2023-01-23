using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public class RedisCache : CacheBase, IRedisCache
{
    private const string LogWarnMessage = "RedisCache exception.";

    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly ISerializerProxy _serializer;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ILogger<RedisCache> _logger;
    private readonly RedisCacheOptions _cacheOptions;
    private readonly bool _supportsExpireTime;
    private readonly IPolicyExecutor _readPolicy;
    private readonly IPolicyExecutor _writePolicy;

    public RedisCache(
        Func<IDatabase> databaseAccessor,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<RedisCacheOptions> optionsAccessor,
        ILogger<RedisCache> logger)
        : base(optionsAccessor.Value)
    {
        _logger = logger;
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _cacheOptions = optionsAccessor.Value;
        _supportsExpireTime = RedisUtils.SupportsExpireTime(_cacheOptions.Version);
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
    }

    private IDatabase Database => _lazyDatabase.Value;

    public string? InstanceName => _cacheOptions.InstanceName;

    public Task<T?> GetAsync<T>(string key, CancellationToken token = default) =>
        GetAsync<T?>(GetRedisKey(key, token));

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, CancellationToken token = default) =>
        GetOrAddAsync(key, generator, _cacheOptions.DefaultExpiration, token);

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(key, generator, ToTimeSpan(expiration), token);

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default)
    {
        var redisKey = GetRedisKey(key, token);

        if (generator == null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        return GetOrAddInternalAsync(redisKey, generator, ToTimeSpan(expiration));
    }

    public Task<bool> RefreshAsync<T>(string key, CancellationToken token = default) =>
        RefreshAsync<T>(key, _cacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(key, ToDateTimeOffset(expiration), token);

    public async Task<bool> RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var redisKey = GetRedisKey(key, token);
        expiration = ToDateTimeOffset(expiration);

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

    public Task<bool> RemoveAsync<T>(string key, CancellationToken token = default) =>
        RemoveAsync(GetRedisKey(key, token));

    public Task<bool> SetAsync<T>(string key, T? value, CancellationToken token = default) =>
        SetAsync(key, value, _cacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetInternalAsync(GetRedisKey(key, token), value, ToTimeSpan(expiration));

    public Task<bool> SetAsync<T>(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        SetInternalAsync(GetRedisKey(key, token), value, ToTimeSpan(expiration));

    public async Task<bool> ContainsAsync(string key, CancellationToken token = default)
    {
        var redisKey = GetRedisKey(key, token);
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

    public async Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default)
    {
        TimeSpan? ret = default;
        var operation = StartOperation();
        try
        {
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyTimeToLiveAsync(GetRedisKey(key, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
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

    public async Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default)
    {
        DateTimeOffset? ret = default;
        var operation = StartOperation();
        try
        {
            if (_supportsExpireTime)
            {
                ret = await _readPolicy.ExecuteAsync(() => Database.KeyExpireTimeAsync(GetRedisKey(key, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
            }
            else
            {
                var timeToLive = await TimeToLiveAsync(key, token);
                ret = timeToLive.HasValue ? Clock.UtcNow.Add(timeToLive.Value) : null;
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

    private RedisKey GetRedisKey(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return CacheUtils.GetKey(key, _cacheOptions.InstanceName);
    }

    private ITelemetryOperation StartOperation([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisCache>(name);

    private ITelemetryOperation StartOperation<T>([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisCache, T>(name);

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);
}

