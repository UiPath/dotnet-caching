using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache : RedisCacheBase, IHashCache
{
    private readonly ILogger<RedisHashCache> _logger;
    private readonly ISerializerProxy<byte[]> _serializer;
    private readonly IMemorySerializerProxy? _memorySerializer;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly bool _supportsExpireTime;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheOptions _cacheOptions;
    private readonly Action<LoggedKey, string, RedisValue>? _auditKeySize;
    private readonly bool _cacheNullValues;

    public RedisHashCache(
        IRedisConnector redis,
        ISerializerProxy<byte[]> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger<RedisHashCache> logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory, clock, keyMaskingPolicy)
    {
        _serializer = serializer;
        _memorySerializer = serializer as IMemorySerializerProxy;
        _logger = logger;
        _read = resiliencePipelineProvider.Get(ResiliencePipelineNames.Read);
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _cacheOptions = cacheOptions;
        _cacheEntryFactory = redisCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _supportsExpireTime = RedisUtils.SupportsExpireTime(redis.Version);
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(_cacheOptions, GetType());
        _cacheNullValues = redisCacheOptions.CacheNullValues;
        if (_cacheOptions.AuditEnabled)
        {
            _auditKeySize = AuditKeySize;
        }
    }

    public string Name => KnownCacheProviderNames.Redis;

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateFieldForRead(field);
        return await GetInnerAsync<T?>(cacheKey, field, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?>(cacheKey, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?>(cacheKey, fields, token);
    }

    public async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerCacheEntryAsync<T?>(cacheKey, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, GetExpiration(policy), HashCacheSetOption.KeyReplace, policy, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, GetExpiration(expiration), HashCacheSetOption.KeyReplace, policy, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, generator, GetExpiration(expiration), setOption ?? HashCacheSetOption.KeyReplace, policy, token);

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            ret = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyExistsAsync(s.Key, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
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

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var expiration = GetExpiration(options, policy);
        var now = Clock.GetUtcNow();
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            if (expiration < now)
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                var transaction = Database.CreateTransaction();
                using (QueueMetadataWrite(transaction, redisKey, options.Metadata))
                {
                    QueueExpirationUpdate(transaction, redisKey, expiration);

                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
                    },
                    transaction,
                    default,
                    token).ConfigureAwait(false);
                }

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
            ret = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
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

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(policy), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var metadata = values.Count > 0 || _cacheNullValues ? options.Metadata : null;
        var expiration = GetExpiration(options, policy);
        var setOption = values.Count == 0 && _cacheNullValues ? HashCacheSetOption.KeyReplace : options.SetOption;

        // Everything that can throw runs before the fields and the metadata rent buffers, which SetInnerAsync then owns.
        var (entries, payloads) = SerializeFields(values, metadata);
        return SetInnerAsync<T>(redisKey, entries, payloads, setOption, expiration, token);
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        TimeSpan? ret = default;
        var operation = StartOperation<T>();
        try
        {
            ret = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyTimeToLiveAsync(s.Self.ToRedisKey(s.CacheKey, token), CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, CacheKey: cacheKey),
            default,
            token).ConfigureAwait(false);
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
                ret = await _read.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireTimeAsync(s.Self.ToRedisKey(s.CacheKey, token), CommandFlags.PreferReplica).AsValueTask();
                },
                (Self: this, CacheKey: cacheKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                var timeToLive = await TimeToLiveAsync<T>(cacheKey, token);
                ret = timeToLive.HasValue ? Clock.ToDateTimeOffset(timeToLive.Value) : null;
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
            var keyExists = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyExistsAsync(s.Key, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            if (keyExists)
            {
                if (metadata.Count > 0)
                {
                    var (metadataValue, payload) = SerializeFieldValue(metadata);
                    using (payload)
                    {
                        if (_cacheNullValues)
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                var transaction = s.Self.Database.CreateTransaction();
                                transaction.AddCondition(Condition.KeyExists(s.Key));
                                transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).Forget();
                                return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                        else
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                await s.Self.Database.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                                return true;
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                    }
                }
                else if (_cacheNullValues)
                {
                    ret = await _write.ExecuteAsync(static async (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var transaction = s.Self.Database.CreateTransaction();
                        transaction.AddCondition(Condition.KeyExists(s.Key));
                        transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, RedisValue.EmptyString, When.Always, CommandFlags.DemandMaster).Forget();
                        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                    },
                    (Self: this, Key: redisKey),
                    default,
                    token).ConfigureAwait(false);
                }
                else
                {
                   ret = await _write.ExecuteAsync(static (s, token) =>
                   {
                       token.ThrowIfCancellationRequested();
                       return s.Self.Database.HashDeleteAsync(s.Key, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).AsValueTask();
                   },
                   (Self: this, Key: redisKey),
                   default,
                   token).ConfigureAwait(false);
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

    private static void QueueExpirationUpdate(ITransaction transaction, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
            return;
        }
        transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
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

    private static void Release(SerializedPayload[] payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Dispose();
        }
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset effectiveExpiration, HashCacheSetOption setOption, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var (found, cached) = await GetInnerWithFoundAsync<T?>(cacheKey, token).ConfigureAwait(false);
        if (found)
        {
            return cached;
        }

        LogCacheMissed(Logged(cacheKey, typeof(T)));
        var wrappedGenerator = WrapWithFactoryTimeout(generator, (policy ?? DefaultPolicy)?.FactoryTimeout, cacheKey);
        var ret = await wrappedGenerator(token).ConfigureAwait(false);
        if (ret.Count > 0)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetAsync(cacheKey, ret, options, policy, token).ConfigureAwait(false);
        }
        else if (_cacheNullValues)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetEmptyMarkerAsync<T>(cacheKey, options, policy, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
        }

        return ret;
    }

    private Func<CancellationToken, Task<IDictionary<string, T?>>> WrapWithFactoryTimeout<T>(Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? factoryTimeout, CacheKey cacheKey)
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero)
        {
            return generator;
        }
        return token => FactoryTimeout.RunAsync(generator, factoryTimeout, cacheKey, Name, Telemetry, token);
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
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAllAsync(s.Key, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);

            if (hashEntries.Length == 0)
            {
                operation.Stop();
                return (false, ret);
            }

            var values = new Dictionary<string, T?>();
            var hasEmptyMarker = false;
            var anyValue = false;
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
                _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), name, v);
                anyValue |= IsCacheHit(v);
                values.Add(name, DeserializeField<T>(v));
            }
            ret = values;
            found = anyValue || (hasEmptyMarker && _cacheNullValues);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return (found, ret);
    }

    private ValueTask<bool> SetEmptyMarkerAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        RedisValue metadata = options.Metadata != null && options.Metadata.Count > 0
            ? _serializer.Serialize(options.Metadata)
            : RedisValue.EmptyString;
        var entries = new[] { new HashEntry(KnownFieldNames.MetadataKey, metadata) };
        var expiration = GetExpiration(options, policy);
        return SetInnerAsync<T>(redisKey, entries, [], HashCacheSetOption.KeyReplace, expiration, token);
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset localExpiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        LogRefreshingKey(Logged(cacheKey, redisKey, typeof(T)), localExpiration);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireAsync(s.Key, s.LocalExpiration.UtcDateTime, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey, LocalExpiration: localExpiration),
                default,
                token).ConfigureAwait(false)
                : await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyPersistAsync(s.Key, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
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

    /// <summary>Returns the metadata's payload, which the caller disposes once the transaction has executed.</summary>
    private SerializedPayload QueueMetadataWrite(ITransaction transaction, RedisKey redisKey, IDictionary<string, string?>? metadata)
    {
        if (metadata != null)
        {
            if (_cacheNullValues)
            {
                transaction.AddCondition(Condition.KeyExists(redisKey));
            }
            var (value, payload) = SerializeFieldValue(metadata);
            var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, value) };
            transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).Forget();
            return payload;
        }
        if (_cacheNullValues)
        {
            transaction.AddCondition(Condition.KeyExists(redisKey));
            var entries = new[] { new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString) };
            transaction.HashSetAsync(redisKey, entries, CommandFlags.DemandMaster).Forget();
            return default;
        }
        transaction.HashDeleteAsync(redisKey, new RedisValue(KnownFieldNames.MetadataKey), CommandFlags.DemandMaster).Forget();
        return default;
    }

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset effective, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var (hashEntries, payloads) = SerializeFields(values);
        return SetInnerAsync<T>(redisKey, hashEntries, payloads, HashCacheSetOption.KeyReplace, effective, token);
    }

    /// <summary>One entry per field, then the metadata entry when there is metadata; the payloads go back once the write has completed.</summary>
    private (HashEntry[] Entries, SerializedPayload[] Payloads) SerializeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata = null)
    {
        var count = metadata is null ? values.Count : values.Count + 1;
        var entries = new HashEntry[count];
        var payloads = new SerializedPayload[count];
        var i = 0;
        try
        {
            foreach (var kv in values)
            {
                (var value, payloads[i]) = SerializeFieldValue(kv.Value);
                entries[i++] = new HashEntry(kv.Key, value);
            }
            if (metadata is not null)
            {
                (var value, payloads[i]) = SerializeFieldValue(metadata);
                entries[i] = new HashEntry(KnownFieldNames.MetadataKey, value);
            }
        }
        catch
        {
            Release(payloads);
            throw;
        }
        return (entries, payloads);
    }

    /// <summary>Borrowed memory is safe here because every write awaits its command and the connection copies the value while writing it; pooled memory goes back only then.</summary>
    private (RedisValue Value, SerializedPayload Payload) SerializeFieldValue<T>(T? value)
    {
        if (_cacheNullValues && IsDefault(value))
        {
            return (RedisValue.EmptyString, default);
        }
        if (_memorySerializer is { } memory)
        {
            var payload = SerializedPayload.Serialize(memory, value);
            return (payload.Memory, payload);
        }
        return (_serializer.Serialize(value), default);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryForKeyAsync<T>(CacheKey cacheKey, RedisKey redisKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Per attempt, since a retry cannot re-run a drained transaction; boxed so the pipeline stays bool.
        var read = new StrongBox<StrongBox<(HashEntry[] Entries, DateTimeOffset? Expiration)>?>();
        var committed = await _read.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            var transaction = s.Self.Database.CreateTransaction();
            var hashEntriesTask = transaction.HashGetAllAsync(s.Key, CommandFlags.PreferReplica);
            Task<DateTime?>? expireTimeTask = s.Self._supportsExpireTime ? transaction.KeyExpireTimeAsync(s.Key, CommandFlags.PreferReplica) : null;
            Task<TimeSpan?>? expireTimeToLiveTask = s.Self._supportsExpireTime ? null : transaction.KeyTimeToLiveAsync(s.Key, CommandFlags.PreferReplica);

            // Observed now: an uncommitted transaction leaves them unawaited.
            hashEntriesTask.Forget();
            expireTimeTask?.Forget();
            expireTimeToLiveTask?.Forget();
            if (!await transaction.ExecuteAsync(CommandFlags.PreferReplica).ConfigureAwait(false))
            {
                return false;
            }

            var entries = await hashEntriesTask.ConfigureAwait(false);
            var expiration = expireTimeTask is not null
                ? (DateTimeOffset?)await expireTimeTask.ConfigureAwait(false)
                : s.Self.Clock.ToDateTimeOffset(await expireTimeToLiveTask!.ConfigureAwait(false));
            Interlocked.CompareExchange(ref s.Read.Value, new((entries, expiration)), null);
            return true;
        },
        (Self: this, Key: redisKey, Read: read),
        default,
        token).ConfigureAwait(false);
        if (!committed || read.Value is not { } result)
        {
            throw new InvalidOperationException("Unable to read from redis");
        }

        return ParseCacheEntry<T>(cacheKey, redisKey, result.Value.Entries, result.Value.Expiration);
    }

    [SuppressMessage("SonarLint.Rule", "S3776")]
    private ICacheEntry<IDictionary<string, T?>> ParseCacheEntry<T>(CacheKey cacheKey, RedisKey redisKey, HashEntry[] hashEntries, DateTimeOffset? expireTime)
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
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), key, v);

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

        return _cacheEntryFactory.Create<IDictionary<string, T?>>(values, Clock.ToDateTimeOffset(expireTime), extendedProps);
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

    private bool IsCacheHit(RedisValue value) =>
        !value.IsNull && (value.Length() != 0 || _cacheNullValues);


    private async ValueTask<T?> GetInnerAsync<T>(CacheKey cacheKey, string field, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return default;
        }
        T? ret = default;
        bool found = false;
        var operation = StartOperation<T>(nameof(GetAsync));
        try
        {
            var value = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAsync(s.Key, s.Field, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey, Field: field),
            RedisValue.Null,
            token).ConfigureAwait(false);
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), field, value);
            ret = DeserializeField<T?>(value);
            found = IsCacheHit(value);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
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
        if (!IsConnected)
        {
            return ret;
        }
        var operation = StartOperation<T>();
        bool found = false;
        try
        {
            var values = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAsync(s.Key, s.Fields.Select(k => (RedisValue)k).ToArray(), CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey, Fields: fields),
            [],
            token).ConfigureAwait(false);
            if (values.Length == fields.Length)
            {
                var dict = new Dictionary<string, T?>(fields.Length);
                var anyPresent = false;
                for (var i = 0; i < fields.Length; i++)
                {
                    var v = values[i];
                    _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), fields[i], v);
                    dict.Add(fields[i], DeserializeField<T?>(v));
                    anyPresent |= IsCacheHit(v);
                }
                ret = dict;
                found = anyPresent;
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
            TrackRead(operation, found, redisKey);
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
        bool found = false;
        try
        {
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAllAsync(s.Key, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);
            if (hashEntries.Length > 0)
            {
                var values = new Dictionary<string, T?>();
                var hasEmptyMarker = false;
                var anyValue = false;
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
                    _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), name, v);
                    anyValue |= IsCacheHit(v);
                    values.Add(name, DeserializeField<T?>(v));
                }
                ret = values;
                found = anyValue || (hasEmptyMarker && _cacheNullValues);
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
            TrackRead(operation, found, redisKey);
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
            var keyExists = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyExistsAsync(s.Key, CommandFlags.PreferReplica).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            if (keyExists)
            {
                ret = await GetCacheEntryForKeyAsync<T?>(cacheKey, redisKey, token).ConfigureAwait(false);
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
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }

    private async ValueTask<bool> SetInnerAsync<T>(RedisKey redisKey, HashEntry[] hashEntries, SerializedPayload[] payloads, HashCacheSetOption setOption, DateTimeOffset expiration, CancellationToken token)
    {
        var now = Clock.GetUtcNow();
        var ret = false;
        if (token.IsCancellationRequested || !IsConnected)
        {
            Release(payloads);
            token.ThrowIfCancellationRequested();
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
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                var transaction = Database.CreateTransaction();
                if (setOption == HashCacheSetOption.KeyReplace)
                {
                    transaction.KeyDeleteAsync(redisKey).Forget();
                }

                transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).Forget();
                if (expiration != DateTimeOffset.MaxValue)
                {
                    await transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
                }
                else if (setOption == HashCacheSetOption.HashReplace)
                {
                    // The key survives a HashReplace, and so would a TTL an earlier write gave it.
                    transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
                }

                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
                },
                transaction,
                default,
                token).ConfigureAwait(false);
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
            Release(payloads);
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

    private TelemetryScope StartOperation<T>([CallerMemberName] string methodName = "") =>
        new(Telemetry, Clock, Name, methodName, typeof(T));


    private void AuditKeySize(LoggedKey key, string field, RedisValue value)
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

    private ICacheEntry<IDictionary<string, T?>> Default<T>() => _cacheEntryFactory.Create(Empty<T?>(), DateTimeOffset.MinValue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing key {RedisKey} at expiration {LocalExpiration}")]
    private partial void LogRefreshingKey(LoggedKey redisKey, DateTimeOffset localExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisHashCache exception.")]
    private partial void LogRedisHashCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis transaction failed.")]
    private partial void LogRedisTransactionFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis large value detected for key {RedisKey}, field {Field}, length {Length}")]
    private partial void LogLargeValueDetected(LoggedKey redisKey, string field, long length);
}
