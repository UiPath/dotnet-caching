using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisHashCache : IHashCache
{
    private const string LogWarnMessage = "RedisHashCache exception.";
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly ILogger<RedisHashCache> _logger;
    private readonly RedisCacheOptions _cacheOptions;
    private readonly ISystemClock _clock;
    private readonly ISerializerProxy _serializer;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IKeyResolver _keyResolver;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IPolicyExecutor _readPolicy;
    private readonly IPolicyExecutor _writePolicy;
    private readonly bool _supportsExpireTime;
    private readonly string _prefix;

    public RedisHashCache(
        Func<IDatabase> databaseAccessor,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        IKeyResolver keyResolver,
        IOptions<RedisCacheOptions> optionsAccessor,
        ILogger<RedisHashCache> logger)
    {
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _keyResolver = keyResolver;
        _logger = logger;
        _cacheOptions = optionsAccessor.Value;
        _clock = _cacheOptions.Clock ?? new SystemClock();
        _cacheEntryFactory = _cacheOptions.EntryFactory ?? new CacheEntryFactory();
        _supportsExpireTime = RedisUtils.SupportsExpireTime(_cacheOptions.Version);
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
        _prefix = _keyResolver.GetKey(_cacheOptions.RedisTypePrefixes.Hash, _cacheOptions.Prefix);
    }

    private IDatabase Database => _lazyDatabase.Value;

    public Task<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default)
    {
        ValidateField(field);
        return GetInnerAsync<T>(cacheKey, field, token);
    }

    public async Task<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        IDictionary<string, T?> ret = Empty<T>();
        var operation = StartOperation<T>();
        try
        {
            var hashEntries = (await _readPolicy.ExecuteAsync(() => Database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false))
                .Where(k => k.Name != Constants.MetadataKey);
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

    public async Task<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        if (fields == null || fields.Length == 0)
        {
            return await GetAsync<T>(cacheKey, token);
        }

        ValidateFields(fields);
        var redisKey = ToRedisKey(cacheKey, token);

        IDictionary<string, T?> ret = Empty<T>();
        var operation = StartOperation<T>();
        try
        {
            var values = await _readPolicy.ExecuteAsync(() => Database.HashGetAsync(redisKey, fields.Select(k => (RedisValue)k).ToArray(), CommandFlags.PreferReplica)).ConfigureAwait(false);
            ret = new Dictionary<string, T?>();
            for (var i = 0; i < fields.Length; i++)
            {
                var v = values[i];
                ret.Add(fields[i], string.IsNullOrWhiteSpace(v) ? default : _serializer.Deserialize<T?>(v));
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

    public async Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
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

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _cacheOptions.DefaultExpiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, ToDateTimeOffset(expiration), token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, expiration, HashCacheSetOption.KeyReplace, token);

    public async Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default)
    {
        var ret = await GetAsync<T?>(cacheKey, token).ConfigureAwait(false);
        if (ret.Any())
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheKey);
        ret = await generator().ConfigureAwait(false);
        if (ret.Any())
        {
            var options = new HashCacheEntryOptions(expiration, default, default, setOption ?? HashCacheSetOption.KeyReplace);
            await SetAsync(cacheKey, ret, options, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
        }

        return ret;
    }

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

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _cacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, ToDateTimeOffset(expiration), token);

    public async Task<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
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

    public async Task<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
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
                if (options.Metadata != null)
                {
                    var hashEntries = new[] { new HashEntry(Constants.MetadataKey, _serializer.Serialize(options.Metadata)) };
                    _ = tran.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                else
                {
                    var field = new RedisValue(Constants.MetadataKey);
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

    public async Task<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
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

    public Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(cacheKey, values, _cacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(cacheKey, values, ToDateTimeOffset(expiration), token);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var hashEntries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToArray();
        return SetInnerAsync<T>(redisKey, hashEntries, HashCacheSetOption.KeyReplace, ToDateTimeOffset(expiration));
    }

    public Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var entries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToList();
        if (entries.Any() && options.Metadata != null)
        {
            entries.Add(new HashEntry(Constants.MetadataKey, _serializer.Serialize(options.Metadata)));
        }

        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);

        return SetInnerAsync<T>(redisKey, entries, options.SetOption, expiration);
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
            operation.Track(ret != null);
        }

        return ret;
    }

    public Task<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        GetInnerAsync<IDictionary<string, string?>>(cacheKey, Constants.MetadataKey, token);

    public async Task<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica)).ConfigureAwait(false);
            if (keyExists)
            {
                ret = metadata.Any()
                    ? await _writePolicy.ExecuteAsync(() => Database.HashSetAsync(redisKey, Constants.MetadataKey, _serializer.Serialize(metadata))).ConfigureAwait(false)
                    : await _writePolicy.ExecuteAsync(() => Database.HashDeleteAsync(redisKey, Constants.MetadataKey, CommandFlags.DemandMaster)).ConfigureAwait(false);
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

    public void Dispose()
    {
        //nothing to dispose
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
            if (string.Equals(key, Constants.MetadataKey))
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

    private async Task<T?> GetInnerAsync<T>(CacheKey cacheKey, string key, CancellationToken token = default)
    {
        var redisKey = ToRedisKey(cacheKey, token);
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

    private async Task<bool> SetInnerAsync<T>(RedisKey redisKey, ICollection<HashEntry> hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration)
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
                if (setOption == HashCacheSetOption.KeyReplace)
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

    private RedisKey ToRedisKey(CacheKey cacheKey, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        return _keyResolver.GetKey(cacheKey, _prefix);
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
        _telemetryProvider.StartOperation<RedisHashCache>(name);

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisHashCache, T>(name);

    private static void Validate<T>(IDictionary<string, T?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateFields(values.Keys);
    }

    private static void ValidateFields(ICollection<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var key in fields)
        {
            ValidateField(key);
        }
    }

    private static void ValidateField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        if (field == Constants.MetadataKey)
        {
            throw new ArgumentException("Reserved key");
        }
    }

    private static IDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;
}
