using UiPath.Caching.Locking;

namespace UiPath.Caching;

internal sealed class MultilayerSetCache : ISetCache
{
    private readonly string _name;
    private readonly ISetCache _inner;
    private readonly IMemoryCache _memoryCache;
    private readonly MemorySetCache _memorySetCache;
    private readonly TimeSpan? _localMaxExpiration;
    private readonly IConnectionState _connectionState;
    private readonly bool _useLocalOnlyWhenDisconnected;
    private readonly TimeSpan? _localMaxExpirationDisconnected;
    private readonly TimeSpan? _defaultExpiration;

    public MultilayerSetCache(
        string name,
        ISetCache inner,
        IMemoryCacheFactory memoryCacheFactory,
        ISerializerProxy<RedisValue> serializer,
        IMemoryCacheOptions memoryOptions,
        ILocalLock localLock,
        TimeSpan? localMaxExpiration,
        bool connectionMonitorEnabled = false,
        TimeSpan? connectionMonitorPeriod = null,
        bool useLocalOnlyWhenDisconnected = false,
        TimeSpan? localMaxExpirationDisconnected = null,
        TimeSpan? defaultExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(memoryCacheFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(memoryOptions);
        ArgumentNullException.ThrowIfNull(localLock);
        _name = name;
        _inner = inner;
        _memoryCache = memoryCacheFactory.Get(memoryOptions);
        _memorySetCache = new MemorySetCache(name, _memoryCache, serializer, localLock, memoryOptions);
        _localMaxExpiration = localMaxExpiration;
        _connectionState = connectionMonitorEnabled ? GetConnectionMonitor(inner, connectionMonitorPeriod) : NullConnectionStateMonitor.Instance;
        _useLocalOnlyWhenDisconnected = useLocalOnlyWhenDisconnected && connectionMonitorEnabled;
        _localMaxExpirationDisconnected = localMaxExpirationDisconnected;
        _defaultExpiration = defaultExpiration;
    }

    private static IConnectionState GetConnectionMonitor(ISetCache inner, TimeSpan? period) =>
        inner is IConnectionState state
            ? new ConnectionStateMonitor(NullTelemetryProvider.Instance, period ?? TimeSpan.FromSeconds(5), state)
            : NullConnectionStateMonitor.Instance;

    public string Name => _name;

    public async ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await InternalAddAsync(cacheKey, item, policy, token).ConfigureAwait(false);
    }

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy = null, CancellationToken token = default) =>
        AddAsync(cacheKey, items, expiration: (DateTimeOffset?)null, policy, token);

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        AddAsync(cacheKey, items, FromTtl(expiration), policy, token);

    public async ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await InternalAddAsync(cacheKey, Materialize(items), expiration, policy, token).ConfigureAwait(false);
    }

    public async ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            var popped = await _memorySetCache.PopAsync<T>(key, 1, token).ConfigureAwait(false);
            return popped.Count > 0 ? popped.First() : default;
        }
        var value = await _inner.PopAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        if (value is not null)
        {
            await _memorySetCache.RemoveAsync(key, [value], CancellationToken.None).ConfigureAwait(false);
        }
        return value;
    }

    public async ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.PopAsync<T>(key, count, token).ConfigureAwait(false);
        }
        var values = await _inner.PopAsync<T>(cacheKey, count, policy, token).ConfigureAwait(false);
        if (values.Count > 0)
        {
            await _memorySetCache.RemoveAsync(key, values.Where(v => v is not null).Select(v => v!), CancellationToken.None).ConfigureAwait(false);
        }
        return values;
    }

    public async ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (_memorySetCache.TryGetMembers<T>(key, out var members))
        {
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                return members;
            }
            _memorySetCache.Remove(key);
            return [];
        }
        var fetched = await _inner.MembersAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        if (fetched.Count > 0)
        {
            await _memorySetCache.ReplaceAsync(key, fetched.Where(m => m is not null).Select(m => m!), LocalExpiration(), CancellationToken.None).ConfigureAwait(false);
        }
        return fetched;
    }

    public async ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (_memorySetCache.TryContainsItem(key, item, out var contains))
        {
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                return contains;
            }
            _memorySetCache.Remove(key);
            return false;
        }
        return await _inner.ContainsItemAsync(cacheKey, item, token).ConfigureAwait(false);
    }

    public async ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (_memorySetCache.TryGetCount(key, out var count))
        {
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                return count;
            }
            _memorySetCache.Remove(key);
            return 0;
        }
        return await _inner.CountAsync<T>(cacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (_memorySetCache.ContainsKey(key))
        {
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                return true;
            }
            _memorySetCache.Remove(key);
            return false;
        }
        return await _inner.ContainsAsync<T>(cacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.RemoveAsync(key, [item], token).ConfigureAwait(false) > 0;
        }
        await _memorySetCache.RemoveAsync(key, [item], token).ConfigureAwait(false);
        return await _inner.RemoveItemAsync(cacheKey, item, token).ConfigureAwait(false);
    }

    public async ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        var materialized = Materialize(items);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.RemoveAsync(key, materialized, token).ConfigureAwait(false);
        }
        await _memorySetCache.RemoveAsync(key, materialized, token).ConfigureAwait(false);
        return await _inner.RemoveItemsAsync(cacheKey, materialized, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.RemoveKeyAsync(key, token).ConfigureAwait(false);
        }
        _memorySetCache.Remove(key);
        return await _inner.RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        if (_connectionState is IDisposable connectionState)
        {
            connectionState.Dispose();
        }
    }

    private bool GetInnerCacheDisconnected() => _inner is NullSetCache || (_useLocalOnlyWhenDisconnected && !_connectionState.IsConnected);

    private DateTimeOffset? LocalWriteExpiration(DateTimeOffset? requested, CachePolicy? policy)
    {
        requested ??= FromTtl(policy?.DistributedExpiration ?? (_inner is NullSetCache ? _defaultExpiration : null));
        return _inner is NullSetCache ? requested : DisconnectedExpiration(requested);
    }

    private async ValueTask<bool> InternalAddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy, CancellationToken token)
    {
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.AddAsync(key, [item], LocalWriteExpiration(null, policy), token).ConfigureAwait(false) > 0;
        }
        var added = await _inner.AddAsync(cacheKey, item, policy, token).ConfigureAwait(false);
        await _memorySetCache.AddAsync(key, [item], CancellationToken.None).ConfigureAwait(false);
        return added;
    }

    private async ValueTask<long> InternalAddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token)
    {
        var key = Key(cacheKey, token);
        if (GetInnerCacheDisconnected())
        {
            return await _memorySetCache.AddAsync(key, items, LocalWriteExpiration(expiration, policy), token).ConfigureAwait(false);
        }
        var added = await _inner.AddAsync(cacheKey, items, expiration, policy, token).ConfigureAwait(false);
        await _memorySetCache.AddAsync(key, items, CancellationToken.None).ConfigureAwait(false);
        return added;
    }

    private DateTimeOffset? DisconnectedExpiration(DateTimeOffset? requested)
    {
        if (!_localMaxExpirationDisconnected.HasValue)
        {
            return requested;
        }
        var cap = DateTimeOffset.UtcNow.Add(_localMaxExpirationDisconnected.Value);
        return requested.HasValue && requested.Value < cap ? requested.Value : cap;
    }

    private static DateTimeOffset? FromTtl(TimeSpan? ttl) =>
        ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null;

    private static IEnumerable<T> Materialize<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items as IReadOnlyCollection<T> ?? items.ToArray();
    }

    private DateTimeOffset? LocalExpiration() =>
        _localMaxExpiration.HasValue ? DateTimeOffset.UtcNow.Add(_localMaxExpiration.Value) : null;

    private static string Key(CacheKey cacheKey, CancellationToken token)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        return cacheKey.Name;
    }
}
