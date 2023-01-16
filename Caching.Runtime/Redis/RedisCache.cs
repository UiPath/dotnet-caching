using Polly;
using static UiPath.Platform.Caching.Redis.Messages;

namespace UiPath.Platform.Caching.Redis;

public class RedisCache : CacheBase, IRedisCache
{
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly ISerializerProxy _serializer;
    private readonly ILogger<RedisCache> _logger;
    private readonly RedisCacheOptions _cacheOptions;
    private readonly bool _supportsExpireTime;
    private readonly IAsyncPolicy _asyncPolicy;


    public RedisCache(
        Func<IDatabase> databaseAccessor,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        IOptions<RedisCacheOptions> optionsAccessor,
        ILogger<RedisCache> logger)
        : base(optionsAccessor.Value)
    {
        _logger = logger;
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _serializer = serializer;
        _cacheOptions = optionsAccessor.Value;
        _supportsExpireTime = RedisUtils.SupportsExpireTime(_cacheOptions.Version);
        _asyncPolicy = policyHolder.AsyncPolicy;
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

    public Task RefreshAsync<T>(string key, CancellationToken token = default) =>
        RefreshAsync<T>(key, _cacheOptions.DefaultExpiration, token);

    public Task RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(key, ToDateTimeOffset(expiration), token);

    public async Task RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var redisKey = GetRedisKey(key, token);
        expiration = ToDateTimeOffset(expiration);

        _logger.LogTrace("Refreshing key {redisKey} at expiration {expiration}", redisKey, expiration);
        try
        {
            if (expiration == DateTimeOffset.MaxValue)
            {
                await _asyncPolicy.ExecuteAsync(() => Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false);
            }
            else
            {
                await _asyncPolicy.ExecuteAsync(() => Database.KeyExpireAsync(redisKey, expiration.Value.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false);
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogWarnMessage);
        }
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
        try
        {
            return await _asyncPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogWarnMessage);
            return false;
        }
    }

    public async Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default) =>
        await _asyncPolicy.ExecuteAsync(() => Database.KeyTimeToLiveAsync(GetRedisKey(key, token), CommandFlags.PreferReplica)).ConfigureAwait(false);

    public async Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default)
    {
        if (_supportsExpireTime)
        {
            return await _asyncPolicy.ExecuteAsync(() => Database.KeyExpireTimeAsync(GetRedisKey(key, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
        }

        var timeToLive = await TimeToLiveAsync(key, token);
        return timeToLive.HasValue ? Clock.UtcNow.Add(timeToLive.Value) : null;
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
        try
        {
            if (IsDefault(value))
            {
                return await RemoveAsync(redisKey).ConfigureAwait(false);
            }

            var serialized = _serializer.Serialize(value);

            return await _asyncPolicy.ExecuteAsync(() => Database.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogWarnMessage);
            return false;
        }
    }

    private async Task<bool> RemoveAsync(RedisKey redisKey)
    {
        try
        {
            return await _asyncPolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogWarnMessage);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(RedisKey redisKey)
    {
        try
        {
            var value = await _asyncPolicy.ExecuteAsync(() => Database.StringGetAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
            return value.IsNullOrEmpty ? default : _serializer.Deserialize<T>(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogWarnMessage);
            return default;
        }
    }

    private RedisKey GetRedisKey(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return CacheUtils.GetKey(key, _cacheOptions.InstanceName);
    }

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);
}
