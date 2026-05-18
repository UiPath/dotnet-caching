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
    private readonly bool _cacheNullValues;

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
        _cacheNullValues = redisCacheOptions.CacheNullValues;
        if (_cacheOptions.AuditEnabled)
        {
            _auditKeySize = AuditKeySize;
        }
    }

    public string Name => KnownCacheProviderNames.Redis;

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field,  CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateFieldForRead(field);
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
        var (found, cached) = await GetInnerWithFoundAsync<T?>(cacheKey, token).ConfigureAwait(false);
        if (found)
        {
            return cached;
        }

        LogCacheMissed(cacheKey);
        var ret = await generator(token).ConfigureAwait(false);
        if (ret.Count > 0)
        {
            var options = new HashCacheEntryOptions(expiration, default, default, setOption ?? HashCacheSetOption.KeyReplace);
            await SetAsync(cacheKey, ret, options, token).ConfigureAwait(false);
        }
        else if (_cacheNullValues)
        {
            var options = new HashCacheEntryOptions(expiration, default, default, setOption ?? HashCacheSetOption.KeyReplace);
            await SetEmptyMarkerAsync<T>(cacheKey, options, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
        }

        return ret;
    }

    private async ValueTask<(bool Found, IDictionary<string, T?> Values)> GetInnerWithFoundAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return (false, Empty<T?>());
        }

        var operation = StartOperation<T>(nameof(GetOrAddAsync));
        bool found = false;
        IDictionary<string, T?> ret = Empty<T?>();
        try
        {
            var hashEntries = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.HashGetAllAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, [], token).ConfigureAwait(false);

            if (hashEntries.Length == 0)
            {
                operation.Stop();
                return (false, ret);
            }

            var values = new Dictionary<string, T?>();
            var hasEmptyMarker = false;
            foreach (var hashEntry in hashEntries)
            {
                var name = hashEntry.Name.ToString();
                if (name == KnownFieldNames.MetadataKey)
                {
                    hasEmptyMarker = hashEntry.Value.Length() == 0;
                    continue;
                }
                if (KnownFieldNames.IsSystemField(name))
                {
                    continue;
                }
                var v = hashEntry.Value;
                _auditKeySize?.Invoke(redisKey, name, v);
                values.Add(name, DeserializeField<T>(v));
            }
            ret = values;
            found = values.Count > 0 || (hasEmptyMarker && _cacheNullValues);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            operation.Track(found);
        }

        return (found, ret);
    }

    private ValueTask<bool> SetEmptyMarkerAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var metadata = options.Metadata != null && options.Metadata.Count > 0
            ? _serializer.Serialize(options.Metadata)
            : RedisValue.EmptyString;
        var entries = new[] { new HashEntry(KnownFieldNames.MetadataKey, metadata) };
        var expiration = options.ExpireTime.HasValue
            ? _clock.ToDateTimeOffset(options.ExpireTime)
            : _clock.ToDateTimeOffset(options.TimeToLive);
        return SetInnerAsync<T>(redisKey, entries, HashCacheSetOption.KeyReplace, expiration, token);
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
                QueueMetadataWrite(transaction, redisKey, options.Metadata);
                QueueExpirationUpdate(transaction, redisKey, expiration);

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

    private void QueueMetadataWrite(ITransaction transaction, RedisKey redisKey, IDictionary<string, string?>? metadata)
    {
        if (metadata != null)
        {
            if (_cacheNullValues)
            {
                transaction.AddCondition(Condition.KeyExists(redisKey));
            }
            var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(metadata)) };
            _ = transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).ConfigureAwait(false);
            return;
        }
        if (_cacheNullValues)
        {
            transaction.AddCondition(Condition.KeyExists(redisKey));
            var entries = new[] { new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString) };
            _ = transaction.HashSetAsync(redisKey, entries, CommandFlags.DemandMaster).ConfigureAwait(false);
            return;
        }
        _ = transaction.HashDeleteAsync(redisKey, new RedisValue(KnownFieldNames.MetadataKey), CommandFlags.DemandMaster).ConfigureAwait(false);
    }

    private static void QueueExpirationUpdate(ITransaction transaction, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            _ = transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
            return;
        }
        _ = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
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
        ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var hashEntries = new HashEntry[values.Count];
        var i = 0;
        foreach (var kv in values)
        {
            hashEntries[i++] = new HashEntry(kv.Key, SerializeFieldValue(kv.Value));
        }
        return SetInnerAsync<T>(redisKey, hashEntries, HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(expiration), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var hasMetadata = options.Metadata != null && (values.Count > 0 || _cacheNullValues);
        var entries = new HashEntry[values.Count + (hasMetadata ? 1 : 0)];
        var i = 0;
        foreach (var kv in values)
        {
            entries[i++] = new HashEntry(kv.Key, SerializeFieldValue(kv.Value));
        }
        if (hasMetadata)
        {
            entries[i] = new HashEntry(KnownFieldNames.MetadataKey, _serializer.Serialize(options.Metadata));
        }

        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);
        var setOption = values.Count == 0 && _cacheNullValues ? HashCacheSetOption.KeyReplace : options.SetOption;

        return SetInnerAsync<T>(redisKey, entries, setOption, expiration, token);
    }

    private RedisValue SerializeFieldValue<T>(T? value)
    {
        if (_cacheNullValues && IsDefault(value))
        {
            return RedisValue.EmptyString;
        }
        return _serializer.Serialize(value);
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
                    var metadataValue = _serializer.Serialize(metadata);
                    if (_cacheNullValues)
                    {
                        ret = await _write.ExecuteAsync(async token =>
                        {
                            token.ThrowIfCancellationRequested();
                            var transaction = Database.CreateTransaction();
                            transaction.AddCondition(Condition.KeyExists(redisKey));
                            _ = transaction.HashSetAsync(redisKey, KnownFieldNames.MetadataKey, metadataValue, When.Always, CommandFlags.DemandMaster);
                            return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                        }, default, token).ConfigureAwait(false);
                    }
                    else
                    {
                        ret = await _write.ExecuteAsync(async token =>
                        {
                            token.ThrowIfCancellationRequested();
                            await Database.HashSetAsync(redisKey, KnownFieldNames.MetadataKey, metadataValue, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                            return true;
                        }, default, token).ConfigureAwait(false);
                    }
                }
                else if (_cacheNullValues)
                {
                    ret = await _write.ExecuteAsync(async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        var transaction = Database.CreateTransaction();
                        transaction.AddCondition(Condition.KeyExists(redisKey));
                        _ = transaction.HashSetAsync(redisKey, KnownFieldNames.MetadataKey, RedisValue.EmptyString, When.Always, CommandFlags.DemandMaster);
                        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
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
        if (hashEntries.Length == 0)
        {
            return Default<T>();
        }

        Dictionary<string, T?> values = [];
        IDictionary<string, string?>? extendedProps = default;
        var hasEmptyMarker = false;

        for (var i = 0; i < hashEntries.Length; i++)
        {
            var hashEntry = hashEntries[i];
            var key = hashEntry.Name.ToString();
            var v = hashEntry.Value;
            _auditKeySize?.Invoke(redisKey, key, v);

            if (string.Equals(key, KnownFieldNames.MetadataKey))
            {
                if (v.Length() == 0)
                {
                    hasEmptyMarker = true;
                }
                else
                {
                    extendedProps = _serializer.Deserialize<IDictionary<string, string?>>(v);
                }
                continue;
            }
            if (KnownFieldNames.IsSystemField(key))
            {
                continue;
            }

            values.Add(key, DeserializeField<T>(v));
        }

        if (values.Count == 0 && (!_cacheNullValues || !hasEmptyMarker))
        {
            return Default<T>();
        }

        return _cacheEntryFactory.Create<IDictionary<string, T?>>(values, _clock.ToDateTimeOffset(expireTime), extendedProps);
    }

    private T? DeserializeField<T>(RedisValue value)
    {
        if (value.IsNull)
        {
            return default;
        }
        if (value.Length() == 0)
        {
            return default;
        }
        var deserialized = _serializer.Deserialize<T>(value);
        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return default;
        }
        return deserialized;
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
            ret = DeserializeField<T?>(value);
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

        ValidateFieldsForRead(fields);
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
                ret.Add(fields[i], DeserializeField<T?>(v));
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
                    var name = hashEntry.Name.ToString();
                    if (KnownFieldNames.IsSystemField(name))
                    {
                        continue;
                    }
                    var v = hashEntry.Value;
                    _auditKeySize?.Invoke(redisKey, name, v);
                    ret.Add(name, DeserializeField<T?>(v));
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

        if (hashEntries.Length == 0 && expiration >= now && _cacheNullValues)
        {
            hashEntries = [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)];
            setOption = HashCacheSetOption.KeyReplace;
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

    private static void ValidateForWrite<T>(IDictionary<string, T?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateFieldsForWrite(values.Keys);
    }

    private static void ValidateFieldsForWrite(ICollection<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var key in fields)
        {
            ValidateFieldForWrite(key);
        }
    }

    private static void ValidateFieldForWrite(string field)
    {
        ValidateFieldShape(field);
        if (KnownFieldNames.IsReserved(field))
        {
            throw new ArgumentException($"Field name '{field}' follows the reserved '_word_' pattern and is reserved for system metadata (e.g. {KnownFieldNames.MetadataKey}).", nameof(field));
        }
    }

    private static void ValidateFieldsForRead(ICollection<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var key in fields)
        {
            ValidateFieldForRead(key);
        }
    }

    private static void ValidateFieldForRead(string field)
    {
        ValidateFieldShape(field);
        if (KnownFieldNames.IsSystemField(field))
        {
            throw new ArgumentException($"Field name '{field}' is reserved for system metadata and cannot be read directly.", nameof(field));
        }
    }

    private static void ValidateFieldShape(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
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
