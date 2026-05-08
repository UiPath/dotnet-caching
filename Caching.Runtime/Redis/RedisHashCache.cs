using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed partial class RedisHashCache : RedisCacheBase, IHashCache
{
    private readonly ILogger<RedisHashCache> _logger;
    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly bool _supportsExpireTime;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheClock _clock;
    private readonly TimeSpan? _defaultExpiration;
    private readonly CacheOptions _cacheOptions;
    private readonly Action<RedisKey, string, RedisValue>? _auditKeySize;

    public RedisHashCache(
        IRedisConnector redis,
        ISerializerProxy<RedisValue> serializer,
        IResiliencePipelineHolder resiliencePipelineHolder,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ILogger<RedisHashCache> logger)
        : base(redis, telemetryProvider, redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled)
    {
        _serializer = serializer;
        _logger = logger;
        _read = resiliencePipelineHolder.Read;
        _write = resiliencePipelineHolder.Write;
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

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _defaultExpiration, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, expiration, HashCacheSetOption.KeyReplace, token);

    public async ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var ret = await GetInnerAsync<T?>(cacheKey, token).ConfigureAwait(false);
        if (ret.Count > 0)
        {
            return ret;
        }

        LogCacheMissed(cacheKey);
        ret = await generator(token).ConfigureAwait(false);
        if (ret.Count > 0)
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
            LogRedisHashCacheException(ex);
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
        LogRefreshingKey(redisKey, localExpiration);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyExpireAsync(redisKey, localExpiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false)
                : await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
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
        var operation = StartOperation<T>();
        try
        {
            if (expiration < now)
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            else
            {
                var transaction = Database.CreateTransaction();
                if (options.Metadata != null)
                {
                    var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata)) };
                    _ = transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                else
                {
                    var field = new RedisValue(KnownFieldNames.MetadataKey);
                    _ = transaction.HashDeleteAsync(redisKey, field, CommandFlags.DemandMaster).ConfigureAwait(false);
                }

                if (expiration != DateTimeOffset.MaxValue)
                {
                    _ = transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }
                else
                {
                    _ = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }

                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);

                if (!ret)
                {
                    LogRedisTransactionFailed();
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
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
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
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
        var hashEntries = new HashEntry[values.Count];
        var i = 0;
        foreach (var kv in values)
        {
            hashEntries[i++] = new HashEntry(kv.Key, _serializer.Serialize(kv.Value));
        }
        return SetInnerAsync<T>(redisKey, hashEntries, HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        Validate(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var hasMetadata = values.Count > 0 && options.Metadata != null;
        var entries = new HashEntry[values.Count + (hasMetadata ? 1 : 0)];
        var i = 0;
        foreach (var kv in values)
        {
            entries[i++] = new HashEntry(kv.Key, _serializer.Serialize(kv.Value));
        }
        if (hasMetadata)
        {
            entries[i] = new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata));
        }

        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);

        return SetInnerAsync<T>(redisKey, entries, options.SetOption, expiration, token);
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
            LogRedisHashCacheException(ex);
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
            LogRedisHashCacheException(ex);
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
            var keyExists = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            if (keyExists)
            {
                if (metadata.Count > 0)
                {
                    ret = await _write.ExecuteAsync(async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        await Database.HashSetAsync(redisKey, KnownFieldNames.MetadataKey, _serializer.Serialize(metadata), When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                        return true;
                    }, default, token).ConfigureAwait(false);
                }
                else
                {
                   ret = await _write.ExecuteAsync(async token =>
                   {
                       token.ThrowIfCancellationRequested();
                       return await Database.HashDeleteAsync(redisKey, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                   }, default, token).ConfigureAwait(false);
                }
            }
            operation.Stop();

        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
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
        var transaction = Database.CreateTransaction();
        var hashEntriesTask = transaction.HashGetAllAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        ConfiguredTaskAwaitable<DateTime?>? expireTimeTask = default;
        ConfiguredTaskAwaitable<TimeSpan?>? expireTimeToLiveTask = default;
        if (_supportsExpireTime)
        {
            expireTimeTask = transaction.KeyExpireTimeAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        }
        else
        {
            expireTimeToLiveTask = transaction.KeyTimeToLiveAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
        }

        var transactionResult = await _read.ExecuteAsync( async token =>
        {
            token.ThrowIfCancellationRequested();
            return await transaction.ExecuteAsync(CommandFlags.PreferReplica).ConfigureAwait(false);
        }, default, token).ConfigureAwait(false);
        if (!transactionResult)
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
        Dictionary<string, T?> values = [];
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
            var value = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.HashGetAsync(redisKey, field, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, RedisValue.Null, token).ConfigureAwait(false);
            _auditKeySize?.Invoke(redisKey, field, value);
            ret = value.IsNullOrEmpty ? default : _serializer.Deserialize<T?>(value);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            operation.Track(ret is not null);
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
            var values = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.HashGetAsync(redisKey, fields.Select(k => (RedisValue)k).ToArray(), CommandFlags.PreferReplica).ConfigureAwait(false);
            },[], token).ConfigureAwait(false);
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
            LogRedisHashCacheException(ex);
        }
        finally
        {
            operation.Track(ret.Count > 0);
        }

        return ret;
    }

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        IDictionary<string, T?> ret = Empty<T?>();

        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>();
        try
        {
            var hashEntries = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, [], token).ConfigureAwait(false);
            if (hashEntries.Length > 0)
            {
                ret = new Dictionary<string, T?>();
                foreach (var hashEntry in hashEntries)
                {
                    if (hashEntry.Name == KnownFieldNames.MetadataKey)
                    {
                        continue;
                    }
                    var v = hashEntry.Value;
                    _auditKeySize?.Invoke(redisKey, hashEntry.Name.ToString(), v);
                    ret.Add(hashEntry.Name.ToString(), v.IsNullOrEmpty ? default : _serializer.Deserialize<T?>(v));
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            operation.Track(ret.Count > 0);
        }

        return ret;
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetInnerCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var ret = Default<T>();
        if(!IsConnected)
        {
            return ret;
        }

        var redisKey = ToRedisKey(cacheKey, token);
        var operation = StartOperation<T>();
        try
        {
            var keyExists = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyExistsAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            if (keyExists)
            {
                ret = await GetCacheEntryForKeyAsync<T?>(redisKey, token).ConfigureAwait(false);
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            operation.Track(ret.Value?.Count > 0);
        }

        return ret;
    }

    private async ValueTask<bool> SetInnerAsync<T>(RedisKey redisKey, HashEntry[] hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var ret = false;
        token.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (expiration < now || hashEntries.Length == 0)
            {
                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
            }
            else
            {
                var transaction = Database.CreateTransaction();
                if (setOption == HashCacheSetOption.KeyReplace)
                {
                    _ = transaction.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                }

                _ = transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
                if (expiration != DateTimeOffset.MaxValue)
                {
                    await transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }

                ret = await _write.ExecuteAsync(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                }, default, token).ConfigureAwait(false);
                if (!ret)
                {
                    LogRedisTransactionFailed();
                }
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
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

    private ITelemetryOperation StartOperation<T>([CallerMemberName] string methodName = "") =>
        Telemetry.StartOperation(Name, typeof(T), methodName);


    private void AuditKeySize(RedisKey key, string field, RedisValue value)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        var valueLen = value.Length();
        if (valueLen > _cacheOptions.LargeValueThreshold)
        {
            LogLargeValueDetected(key, field, valueLen);
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

    private ICacheEntry<IDictionary<string, T?>> Default<T>() => _cacheEntryFactory.Create(Empty<T?>(), DateTimeOffset.MinValue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {LocalExpiration}")]
    private partial void LogRefreshingKey(RedisKey redisKey, DateTimeOffset localExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisHashCache exception.")]
    private partial void LogRedisHashCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis transaction failed.")]
    private partial void LogRedisTransactionFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, field {Field}, length {Length}")]
    private partial void LogLargeValueDetected(RedisKey redisKey, string field, long length);
}
