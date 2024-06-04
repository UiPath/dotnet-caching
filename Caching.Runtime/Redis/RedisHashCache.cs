using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisHashCache : RedisCacheBase, IHashCache
{
    private const string LogWarnMessage = "RedisHashCache exception.";
    private readonly IRedisConnector _redis;
    private readonly ILogger<RedisHashCache> _logger;
    private readonly ISerializerProxy _serializer;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IPolicyExecutor _readPolicy;
    private readonly IPolicyExecutor _writePolicy;
    private readonly bool _supportsExpireTime;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheClock _clock;
    private readonly TimeSpan? _defaultExpiration;
    private readonly CacheOptions _cacheOptions;
    private readonly Action<RedisKey, string, RedisValue>? _auditKeySize;
    private readonly IConnectionState _connectionState;

    public RedisHashCache(
        IRedisConnector redis,
        ISerializerProxy serializer,
        IPolicyHolder policyHolder,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ILogger<RedisHashCache> logger)
        : base(redis, redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled)
    {
        _redis = redis;
        _serializer = serializer;
        _telemetryProvider = telemetryProvider;
        _logger = logger;
        _readPolicy = policyHolder.Read;
        _writePolicy = policyHolder.Write;
        _cacheOptions = cacheOptions;
        _cacheEntryFactory = redisCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _supportsExpireTime = RedisUtils.SupportsExpireTime(redis.Version);
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(_cacheOptions, GetType());
        _defaultExpiration = redisCacheOptions.DefaultExpiration;
        _clock = new CacheClock(redisCacheOptions.Clock, _defaultExpiration);
        if (_cacheOptions.AuditEnabled)
        {
            _auditKeySize = AuditKeySize;
        }

        var connectionMonitorEnabled = redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = connectionMonitorEnabled ? new ConnectionStateMonitor(redis) : NullConnectionStateMonitor.Instance;
    }

    public string Name => KnownCacheProviderNames.Redis;

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field,  CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateField(field);
        return await GetInnerAsync<T?>(cacheKey, field, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?>(cacheKey, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields,CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?>(cacheKey, fields, token);
    }

    public async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey,CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerCacheEntryAsync<T?>(cacheKey, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _defaultExpiration, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, expiration, HashCacheSetOption.KeyReplace, token);

    public async ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var ret = await GetInnerAsync<T?>(cacheKey, token).ConfigureAwait(false);
        if (ret.Any())
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {cacheKey}", cacheKey);
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

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _defaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var localExpiration = _clock.ToDateTimeOffset(expiration);
        _logger.LogTrace("Refreshing key {redisKey} at expiraton {expiration}", redisKey, localExpiration);
        var ret = false;
        var operation = StartOperation();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _writePolicy.ExecuteAsync(() => Database.KeyExpireAsync(redisKey, localExpiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget), token).ConfigureAwait(false)
                : await _writePolicy.ExecuteAsync(() => Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget), token).ConfigureAwait(false);
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

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);
        var now = _clock.UtcNow;
        var ret = false;
        var operation = StartOperation();
        try
        {
            if (expiration < now)
            {
                ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster), token).ConfigureAwait(false);
            }
            else
            {
                var tran = Database.CreateTransaction();
                if (options.Metadata != null)
                {
                    var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata)) };
                    _ = tran.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                else
                {
                    var field = new RedisValue(KnownFieldNames.MetadataKey);
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

                ret = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync(), token).ConfigureAwait(false);
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

    public async ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
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

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default)=>
        SetAsync(cacheKey, values, _defaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default)=>
        SetAsync(cacheKey, values, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var hashEntries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToArray();
        return SetInnerAsync<T>(redisKey, hashEntries, HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var entries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToList();
        if (entries.Count > 0 && options.Metadata != null)
        {
            entries.Add(new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata)));
        }

        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);

        return SetInnerAsync<T>(redisKey, entries, options.SetOption, expiration, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) where T : struct
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var entries = values.Select(kv => new HashEntry(kv.Key, _serializer.Serialize(kv.Value))).ToList();
        if (entries.Count > 0 && options.Metadata != null)
        {
            entries.Add(new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata)));
        }

        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);

        return SetInnerAsync<T>(redisKey, entries, options.SetOption, expiration, token);
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
            operation.Track(ret != null);
        }

        return ret;
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetInnerAsync<IDictionary<string, string?>>(cacheKey, KnownFieldNames.MetadataKey, token);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica), token).ConfigureAwait(false);
            if (keyExists)
            {
                if (metadata.Any())
                {
                    ret = await _writePolicy.ExecuteAsync(async () =>
                    {
                        await Database.HashSetAsync(redisKey, KnownFieldNames.MetadataKey, _serializer.Serialize(metadata), When.Always, CommandFlags.DemandMaster);
                        return true;
                    }, token).ConfigureAwait(false);
                }
                else
                {
                   ret = await _writePolicy.ExecuteAsync(() => Database.HashDeleteAsync(redisKey, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster), token).ConfigureAwait(false);
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

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryForKeyAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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

        var tranResult = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync(), token).ConfigureAwait(false);
        if (!tranResult)
        {
            throw new InvalidOperationException("Unable to read from redis");
        }

        var hashEntries = await hashEntriesTask;
        var expireTime = _supportsExpireTime
            ? (DateTimeOffset?)await expireTimeTask!.Value
            : _clock.ToDateTimeOffset(await expireTimeToLiveTask!.Value);

        return ParseCacheEntry<T>(redisKey, hashEntries, expireTime);
    }

    [SuppressMessage("SonarLint.Rule", "S3776")]
    private ICacheEntry<IDictionary<string, T?>> ParseCacheEntry<T>(RedisKey redisKey, HashEntry[] hashEntries, DateTimeOffset? expireTime)
    {
        Dictionary<string, T?> values = new();
        IDictionary<string, string?>? extendedProps = default;

        for (var i = 0; i < hashEntries.Length; i++)
        {
            var hashEntry = hashEntries[i];
            var key = hashEntry.Name.ToString();
            var v = hashEntry.Value;
            _auditKeySize?.Invoke(redisKey, key, v);

            if (string.Equals(key, KnownFieldNames.MetadataKey))
            {
                extendedProps = _serializer.Deserialize<IDictionary<string, string?>>(v);
                if (i + 1 < hashEntries.Length)
                {
                    for (var j = i + 1; j < hashEntries.Length; j++)
                    {
                        hashEntry = hashEntries[j];
                        key = hashEntry.Name.ToString();
                        v = hashEntry.Value;
                        _auditKeySize?.Invoke(redisKey, key, v);
                        values.Add(key, v.IsNullOrEmpty ? default : _serializer.Deserialize<T>(hashEntry.Value));
                    }
                }
                break;
            }

            values.Add(key, v.IsNullOrEmpty ? default : _serializer.Deserialize<T>(hashEntry.Value));
        }

        return _cacheEntryFactory.Create<IDictionary<string, T?>>(values, _clock.ToDateTimeOffset(expireTime), extendedProps);
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheKey cacheKey, string field, CancellationToken token) 
    {
        var redisKey = ToRedisKey(cacheKey, token);
        T? ret = default;
        var operation = StartOperation<T>(nameof(GetAsync));
        try
        {
            var value = await _readPolicy.ExecuteAsync(() => Database.HashGetAsync(redisKey, field, CommandFlags.PreferReplica), token).ConfigureAwait(false);
            _auditKeySize?.Invoke(redisKey, field, value);
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

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token)
    {
        if (fields == null || fields.Length == 0)
        {
            return await GetInnerAsync<T>(cacheKey, token);
        }

        ValidateFields(fields);
        var redisKey = ToRedisKey(cacheKey, token);

        IDictionary<string, T?> ret = Empty<T?>();
        var operation = StartOperation<T>();
        try
        {
            var values = await _readPolicy.ExecuteAsync(() => Database.HashGetAsync(redisKey, fields.Select(k => (RedisValue)k).ToArray(), CommandFlags.PreferReplica), token).ConfigureAwait(false);
            ret = new Dictionary<string, T?>();
            for (var i = 0; i < fields.Length; i++)
            {
                var v = values[i];
                _auditKeySize?.Invoke(redisKey, fields[i], v);
                ret.Add(fields[i], v.IsNullOrEmpty ? default : _serializer.Deserialize<T?>(v));
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

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        IDictionary<string, T?> ret = Empty<T?>();
        var operation = StartOperation<T>();
        try
        {
            var hashEntries = (await _readPolicy.ExecuteAsync(() =>
            Database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica), token).ConfigureAwait(false))
                .Where(k => k.Name != KnownFieldNames.MetadataKey);
            if (hashEntries.Any())
            {
                ret = new Dictionary<string, T?>();
                foreach (var hashEntry in hashEntries)
                {
                    var v = hashEntry.Value;
                    _auditKeySize?.Invoke(redisKey, hashEntry.Name.ToString(), v);
                    ret.Add(hashEntry.Name.ToString(),v.IsNullOrEmpty ? default : _serializer.Deserialize<T?>(v));
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
            operation.Track(ret.Any());
        }

        return ret;
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetInnerCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        ICacheEntry<IDictionary<string, T?>> ret = _cacheEntryFactory.Create(Empty<T?>(), DateTimeOffset.MinValue);
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _readPolicy.ExecuteAsync(() => Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica), token).ConfigureAwait(false);
            if (keyExists)
            {
                ret = await GetCacheEntryForKeyAsync<T?>(redisKey, token).ConfigureAwait(false);
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

    private async ValueTask<bool> SetInnerAsync<T>(RedisKey redisKey, ICollection<HashEntry> hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var ret = false;
        token.ThrowIfCancellationRequested();
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (expiration < now || hashEntries.Count == 0)
            {
                ret = await _writePolicy.ExecuteAsync(() => Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster), token).ConfigureAwait(false);
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

                ret = await _writePolicy.ExecuteAsync(() => tran.ExecuteAsync(), token).ConfigureAwait(false);
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
        return _redisKeyStrategy.GetRedisKey(cacheKey);
    }

    private ITelemetryOperation StartOperation([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisHashCache>(name);

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string name = "") =>
        _telemetryProvider.StartOperation<RedisHashCache, T>(name);

    private void AuditKeySize(RedisKey key, string field, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _cacheOptions.LargeValueThreshold)
        {
            _logger.LogWarning("Redis large value detected for key {redisKey}, field {field}, length {length}", key, field, valueLen);
        }
    }

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

        if (field == KnownFieldNames.MetadataKey)
        {
            throw new ArgumentException("Reserved key");
        }
    }

    private static ImmutableDictionary<string, T?> Empty<T>() => ImmutableDictionary<string, T?>.Empty;
}
