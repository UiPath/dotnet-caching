using System.Runtime.CompilerServices;

namespace UiPath.Caching.Redis;

public sealed partial class RedisSetCache : RedisCacheBase, ISetCache
{
    private const string RedisSetKeyPrefix = "se";

    private readonly ILogger<RedisSetCache> _logger;
    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly IResiliencePipeline _read;
    private readonly IResiliencePipeline _write;
    private readonly IResiliencePipeline _pop;
    private readonly IRedisKeyStrategy _redisKeyStrategy;
    private readonly CacheOptions _cacheOptions;

    public RedisSetCache(
        IRedisConnector redis,
        ISerializerProxy<RedisValue> serializer,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        RedisSetCacheOptions setCacheOptions,
        ICachePolicyFactory policyFactory,
        ILogger<RedisSetCache> logger)
        : base(redis, telemetryProvider, redisCacheOptions, cacheOptions, policyFactory)
    {
        _serializer = serializer;
        _logger = logger;
        _read = resiliencePipelineProvider.Get(ResiliencePipelineNames.Read);
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _pop = resiliencePipelineProvider.Get(setCacheOptions.ResilienceKeyName);
        _cacheOptions = cacheOptions;
        _redisKeyStrategy = (redisCacheOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(_cacheOptions, RedisSetKeyPrefix);
    }

    public string Name => KnownCacheProviderNames.Redis;

    public async ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var value = _serializer.Serialize(item);
        var added = await AddManyInnerAsync<T>(cacheKey, [value], ResolveExpiration((DateTimeOffset?)null, policy), token).ConfigureAwait(false);
        return added > 0;
    }

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy = null, CancellationToken token = default) =>
        AddAsync(cacheKey, items, expiration: (TimeSpan?)null, policy, token);

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(items);
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        return AddManyInnerAsync<T>(cacheKey, values, Clock.ToDateTimeOffset(ResolveExpiration(expiration, policy)), token);
    }

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(items);
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        return AddManyInnerAsync<T>(cacheKey, values, ResolveExpiration(expiration, policy), token);
    }

    private async ValueTask<long> AddManyInnerAsync<T>(CacheKey cacheKey, RedisValue[] values, DateTimeOffset expiration, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var now = Clock.UtcNow;
        long ret = 0;
        if (!IsConnected || values.Length == 0)
        {
            return ret;
        }

        if (expiration < now)
        {
            _ = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            return ret;
        }

        var operation = StartOperation<T>(nameof(AddAsync));
        try
        {
            var transaction = Database.CreateTransaction();
            var addTask = transaction.SetAddAsync(redisKey, values, CommandFlags.DemandMaster);
            QueueExpirationUpdate(transaction, redisKey, expiration);

            var committed = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);

            if (committed)
            {
                ret = await addTask.ConfigureAwait(false);
            }
            else
            {
                LogRedisTransactionFailed();
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret > 0);
        }

        return ret;
    }

    public async ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        T? ret = default;
        var found = false;
        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>();
        try
        {
            var value = await _pop.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetPopAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, RedisValue.Null, token).ConfigureAwait(false);
            if (!value.IsNull)
            {
                ret = _serializer.Deserialize<T>(value);
                found = true;
            }
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(found);
        }

        return ret;
    }

    public async ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (count <= 0)
        {
            return [];
        }

        var redisKey = ToRedisKey(cacheKey, token);
        IReadOnlyCollection<T?> ret = [];
        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>();
        try
        {
            var values = await _pop.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetPopAsync(redisKey, count, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, Array.Empty<RedisValue>(), token).ConfigureAwait(false);
            ret = Deserialize<T>(values);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret.Count > 0);
        }

        return ret;
    }

    public async ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        IReadOnlyCollection<T?> ret = [];
        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>();
        try
        {
            var values = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetMembersAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, Array.Empty<RedisValue>(), token).ConfigureAwait(false);
            ret = Deserialize<T>(values);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret.Count > 0);
        }

        return ret;
    }

    public async ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var value = _serializer.Serialize(item);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            ret = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetContainsAsync(redisKey, value, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret);
        }

        return ret;
    }

    public async ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        long ret = 0;
        var operation = StartOperation<T>();
        try
        {
            ret = await _read.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetLengthAsync(redisKey, CommandFlags.PreferReplica).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret > 0);
        }

        return ret;
    }

    public async ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var value = _serializer.Serialize(item);
        var ret = false;
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetRemoveAsync(redisKey, value, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret);
        }

        return ret;
    }

    public async ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(items);
        var redisKey = ToRedisKey(cacheKey, token);
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        long ret = 0;
        if (values.Length == 0)
        {
            return ret;
        }

        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Database.SetRemoveAsync(redisKey, values, CommandFlags.DemandMaster).ConfigureAwait(false);
            }, default, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret > 0);
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
            LogRedisSetCacheException(ex);
        }
        finally
        {
            operation.Track(ret);
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
            LogRedisSetCacheException(ex);
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
            _ = transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
            return;
        }
        _ = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private IReadOnlyCollection<T?> Deserialize<T>(RedisValue[] values)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }
        var list = new List<T?>(values.Length);
        foreach (var v in values)
        {
            if (v.IsNull)
            {
                continue;
            }
            list.Add(_serializer.Deserialize<T>(v));
        }
        return list;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisSetCache exception.")]
    private partial void LogRedisSetCacheException(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis transaction failed.")]
    private partial void LogRedisTransactionFailed();
}
