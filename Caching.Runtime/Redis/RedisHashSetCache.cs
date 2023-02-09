using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public class RedisHashSetCache : IRedisRegionCache
{
    private const string LogWarnMessage = "RedisHashSetCache exception.";
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly ILogger<RedisHashSetCache> _logger;
    private readonly RedisCacheOptions _cacheOptions;
    private readonly ISystemClock _clock;
    private readonly ISerializerProxy _serializer;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly bool _supportsExpireTime;
    private readonly IPolicyExecutor _readPolicy;
    private readonly IPolicyExecutor _writePolicy;

    public RedisHashSetCache(
        Func<IDatabase> databaseAccessor,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<RedisCacheOptions> optionsAccessor,
        ILogger<RedisHashSetCache> logger)
    {
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _logger = logger;
        _cacheOptions = optionsAccessor.Value;
        _clock = _cacheOptions.Clock ?? new SystemClock();
        _cacheEntryFactory = _cacheOptions.EntryFactory ?? new CacheEntryFactory();
        _supportsExpireTime = RedisUtils.SupportsExpireTime(_cacheOptions.Version);
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
    }

    public string? InstanceName => _cacheOptions.InstanceName;

    private IDatabase Database => _lazyDatabase.Value;

    public Task<T?> GetItemAsync<T>(Region region, string key, CancellationToken token = default)
    {
        ValidateKey(key);
        return GetInnerAsync<T>(region, key, token);
    }

    public async Task<IDictionary<string, T?>> GetAsync<T>(Region region, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        IDictionary<string, T?> ret = Empty<T>();
        var operation = StartOperation<T>();
        try
        {
            var hashEntries = (await _readPolicy.ExecuteAsync(() => Database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false))
                .Where(k => k.Name != CacheConstants.ExtendedPropertiesKey);
            ret = hashEntries.Any()
                ? hashEntries.ToDictionary(he => he.Name.ToString(), he => _serializer.Deserialize<T?>(he.Value))
                : Empty<T>();
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            _logger.LogWarning(ex, LogWarnMessage);
        }
        finally
        {
            operation.Track(ret.Any());
        }

        return ret;
    }

    public async Task<IDictionary<string, T?>> GetAsync<T>(Region region, string[] keys, CancellationToken token = default)
    {
        if (keys == null || keys.Length == 0)
        {
            return await GetAsync<T>(region, token);
        }

        ValidateKeys(keys);
        var redisKey = BuildRedisRegionKey(region, token);

        IDictionary<string, T?> ret = Empty<T>();
        var operation = StartOperation<T>();
        try
        {
            var values = await _readPolicy.ExecuteAsync(() => Database.HashGetAsync(redisKey, keys.Select(k => (RedisValue)k).ToArray(), CommandFlags.PreferReplica)).ConfigureAwait(false);
            ret = new Dictionary<string, T?>();
            for (var i = 0; i < keys.Length; i++)
            {
                var v = values[i];
                ret.Add(keys[i], string.IsNullOrWhiteSpace(v) ? default : _serializer.Deserialize<T?>(v));
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
            operation.Track(ret.Any());
        }

        return ret;
    }

    public async Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Region region, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        ICacheEntry<IDictionary<string, T?>> ret = _cacheEntryFactory.Create(Empty<T>(), DateTimeOffset.MinValue);
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
            if (keyExists)
            {
                ret = await GetCacheEntryForKeyAsync<T>(redisKey).ConfigureAwait(false);
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
            operation.Track(ret.Value?.Any() ?? false);
        }

        return ret;
    }

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, _cacheOptions.DefaultExpiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, ToDateTimeOffset(expiration), token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, expiration, RegionCacheSetOption.KeyReplace, token);

    public async Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default)
    {
        var ret = await GetAsync<T?>(region, token).ConfigureAwait(false);
        if (ret.Any())
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {region}", region);
        ret = await generator().ConfigureAwait(false);
        if (ret.Any())
        {
            var options = new RegionCacheEntryOptions(expiration, default, default, setOption);
            await SetAsync(region, ret, options, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(region, token).ConfigureAwait(false);
        }

        return ret;
    }

    public async Task<bool> ContainsAsync(Region region, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
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

    public Task<bool> RefreshAsync<T>(Region region, CancellationToken token = default) =>
        RefreshAsync<T>(region, _cacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(Region region, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(region, ToDateTimeOffset(expiration), token);

    public async Task<bool> RefreshAsync<T>(Region region, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        var localExpiration = ToDateTimeOffset(expiration);
        _logger.LogTrace("Refreshing key {redisKey} at expiraton {expiration}", redisKey, localExpiration);
        var ret = false;
        var operation = StartOperation();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _writePolicy.ExecuteAsync(() => Database.KeyExpireAsync(redisKey, localExpiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false)
                : await _writePolicy.ExecuteAsync(() => Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget)).ConfigureAwait(false);
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

    public async Task<bool> RefreshAsync<T>(Region region, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);
        var now = _clock.UtcNow;
        var ret = false;
        var operation = StartOperation();
        try
        {
            if (expiration < now)
            {
                ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
            }
            else
            {
                var tran = Database.CreateTransaction();
                if (options.ExtendedProperties != null)
                {
                    var hashEntries = new[] { new HashEntry(CacheConstants.ExtendedPropertiesKey, _serializer.Serialize(options.ExtendedProperties)) };
                    _ = tran.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                else
                {
                    var field = new RedisValue(CacheConstants.ExtendedPropertiesKey);
                    _ = tran.HashDeleteAsync(redisKey, field, CommandFlags.DemandMaster).ConfigureAwait(false);
                }

                if (expiration != DateTimeOffset.MaxValue)
                {
                    _ = tran.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }
                else
                {
                    _ = tran.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }

                ret = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync()).ConfigureAwait(false);
                if (!ret)
                {
                    _logger.LogWarning("Redis transaction failed.");
                }
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

    public async Task<bool> RemoveAsync<T>(Region region, string key, CancellationToken token = default)
    {
        ValidateKey(key);
        var redisKey = BuildRedisRegionKey(region, token);
        var ret = false;
        var operation = StartOperation();
        try
        {
            ret = await _writePolicy.ExecuteAsync(() => Database.HashDeleteAsync(redisKey, key, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    public async Task<bool> RemoveAsync<T>(Region region, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        var ret = false;
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

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(region, values, _cacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(region, values, ToDateTimeOffset(expiration), token);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        Validate(values);
        var redisKey = BuildRedisRegionKey(region, token);
        var hashEntries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToArray();
        return SetInnerAsync<T>(redisKey, hashEntries, RegionCacheSetOption.KeyReplace, ToDateTimeOffset(expiration));
    }

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        Validate(values);
        var redisKey = BuildRedisRegionKey(region, token);
        var entries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToList();
        if (entries.Any() && options.ExtendedProperties != null)
        {
            entries.Add(new HashEntry(CacheConstants.ExtendedPropertiesKey, _serializer.Serialize(options.ExtendedProperties)));
        }

        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);

        return SetInnerAsync<T>(redisKey, entries, options.SetOption, expiration);
    }

    public async Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default)
    {
        TimeSpan? ret = default;
        var operation = StartOperation();
        try
        {
            ret = await _readPolicy.ExecuteAsync(() => Database.KeyTimeToLiveAsync(BuildRedisRegionKey(region, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
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

    public async Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default)
    {
        DateTimeOffset? ret = default;
        var operation = StartOperation();
        try
        {
            if (_supportsExpireTime)
            {
                ret = await _readPolicy.ExecuteAsync(() => Database.KeyExpireTimeAsync(BuildRedisRegionKey(region, token), CommandFlags.PreferReplica)).ConfigureAwait(false);
            }
            else
            {
                var timeToLive = await TimeToLiveAsync(region, token);
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
            operation.Track(ret != null);
        }

        return ret;
    }

    public Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default) =>
        GetInnerAsync<IDictionary<string, string?>>(region, CacheConstants.ExtendedPropertiesKey, token);

    public async Task<bool> SetExtendedPropertiesAsync<T>(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
            if (keyExists)
            {
                ret = extendedProperties.Any()
                    ? await _writePolicy.ExecuteAsync(() => Database.HashSetAsync(redisKey, CacheConstants.ExtendedPropertiesKey, _serializer.Serialize(extendedProperties))).ConfigureAwait(false)
                    : await _writePolicy.ExecuteAsync(() => Database.HashDeleteAsync(redisKey, CacheConstants.ExtendedPropertiesKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    private async Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryForKeyAsync<T>(RedisKey redisKey)
    {
        var tran = Database.CreateTransaction();
        var hashEntriesTask = tran.HashGetAllAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        ConfiguredTaskAwaitable<DateTime?>? expireTimeTask = default;
        ConfiguredTaskAwaitable<TimeSpan?>? expireTimeToLiveTask = default;
        if (_supportsExpireTime)
        {
            expireTimeTask = tran.KeyExpireTimeAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        }
        else
        {
            expireTimeToLiveTask = tran.KeyTimeToLiveAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        }

        var tranResult = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync()).ConfigureAwait(false);
        if (!tranResult)
        {
            throw new InvalidOperationException("Unable to read from redis");
        }

        var hashEntries = await hashEntriesTask;
        var expireTime = _supportsExpireTime
            ? (DateTimeOffset?)await expireTimeTask!.Value
            : ToDateTimeOffset(await expireTimeToLiveTask!.Value);

        return ParseCacheEntry<T>(hashEntries, expireTime);
    }

    private ICacheEntry<IDictionary<string, T?>> ParseCacheEntry<T>(HashEntry[] hashEntries, DateTimeOffset? expireTime)
    {
        Dictionary<string, T?> values = new();
        IDictionary<string, string?>? extendedProps = default;

        for (var i = 0; i < hashEntries.Length; i++)
        {
            var hashEntry = hashEntries[i];
            var key = hashEntry.Name.ToString();
            if (string.Equals(key, CacheConstants.ExtendedPropertiesKey))
            {
                extendedProps = _serializer.Deserialize<IDictionary<string, string?>>(hashEntry.Value);
                if (i + 1 < hashEntries.Length)
                {
                    for (var j = i + 1; j < hashEntries.Length; j++)
                    {
                        hashEntry = hashEntries[j];
                        values.Add(hashEntry.Name.ToString(), _serializer.Deserialize<T>(hashEntry.Value));
                    }
                }
                break;
            }

            values.Add(key, _serializer.Deserialize<T>(hashEntry.Value));
        }

        return _cacheEntryFactory.Create<IDictionary<string, T?>>(values, ToDateTimeOffset(expireTime), extendedProps);
    }

    private async Task<T?> GetInnerAsync<T>(Region region, string key, CancellationToken token = default)
    {
        var redisKey = BuildRedisRegionKey(region, token);
        T? ret = default;
        var operation = StartOperation<T>(nameof(GetAsync));
        try
        {
            var value = await _readPolicy.ExecuteAsync(() => Database.HashGetAsync(redisKey, key, CommandFlags.PreferReplica)).ConfigureAwait(false);
            ret = value.IsNullOrEmpty ? default : _serializer.Deserialize<T?>(value);
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

    private async Task<bool> SetInnerAsync<T>(RedisKey redisKey, ICollection<HashEntry> hashEntries, RegionCacheSetOption setOption, DateTimeOffset expiration)
    {
        var now = _clock.UtcNow;
        var ret = false;
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (expiration < now || !hashEntries.Any())
            {
                ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
            }
            else
            {
                var tran = Database.CreateTransaction();
                if (setOption == RegionCacheSetOption.KeyReplace)
                {
                    _ = tran.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                }
                
                _ = tran.HashSetAsync(redisKey, hashEntries.ToArray(), CommandFlags.DemandMaster).ConfigureAwait(false);
                if (expiration != DateTimeOffset.MaxValue)
                {
                    await tran.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }

                ret = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync()).ConfigureAwait(false);
                if (!ret)
                {
                    _logger.LogWarning("Redis transaction failed.");
                }
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

    private RedisKey BuildRedisRegionKey(Region key, CancellationToken token = default)
    {
        if (key.IsNull)
        {
            throw new ArgumentNullException(nameof(key));
        }
        token.ThrowIfCancellationRequested();
        return CacheUtils.GetKey(key, InstanceName);
    }

    private DateTimeOffset ToDateTimeOffset(TimeSpan? timeSpan)
    {
        if (_cacheOptions.DefaultExpiration.HasValue)
        {
            return _clock.UtcNow.Add(timeSpan ?? _cacheOptions.DefaultExpiration.Value);
        }

        return timeSpan.HasValue ? _clock.UtcNow.Add(timeSpan.Value) : DateTimeOffset.MaxValue;
    }

    private DateTimeOffset ToDateTimeOffset(DateTimeOffset? dateTimeOffset) =>
        dateTimeOffset ?? (_cacheOptions.DefaultExpiration.HasValue ? _clock.UtcNow.Add(_cacheOptions.DefaultExpiration.Value) : DateTimeOffset.MaxValue);

    private ITelemetryOperation StartOperation([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisHashSetCache>(name);

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisHashSetCache, T>(name);

    private static void Validate<T>(IDictionary<string, T?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateKeys(values.Keys);
    }

    private static void ValidateKeys(ICollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            ValidateKey(key);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (key == CacheConstants.ExtendedPropertiesKey)
        {
            throw new ArgumentException("Reserved key");
        }
    }

    private static IDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;
}
