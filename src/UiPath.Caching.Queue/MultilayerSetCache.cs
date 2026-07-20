using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace UiPath.Caching;

internal sealed class MultilayerSetCache : ISetCache
{
    private readonly string _name;
    private readonly ISetCache _inner;
    private readonly IMemoryCache _memoryCache;
    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly bool _trackSize;
    private readonly TimeSpan? _localMaxExpiration;
    private readonly IConnectionState _connectionState;
    private readonly bool _useLocalOnlyWhenDisconnected;
    private readonly TimeSpan? _localMaxExpirationDisconnected;
    private readonly TimeSpan? _defaultExpiration;
    private readonly bool _localIsStore;
    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);
    private sealed record Snapshot(ImmutableHashSet<RedisValue> Members, DateTimeOffset? Expiration);

    public MultilayerSetCache(
        string name,
        ISetCache inner,
        IMemoryCacheFactory memoryCacheFactory,
        ISerializerProxy<RedisValue> serializer,
        IMemoryCacheOptions memoryOptions,
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
        _name = name;
        _inner = inner;
        _memoryCache = memoryCacheFactory.Get(memoryOptions);
        _serializer = serializer;
        _trackSize = memoryOptions.SizeLimit.HasValue;
        _localMaxExpiration = localMaxExpiration;
        _connectionState = connectionMonitorEnabled ? GetConnectionMonitor(inner, connectionMonitorPeriod) : NullConnectionStateMonitor.Instance;
        _useLocalOnlyWhenDisconnected = useLocalOnlyWhenDisconnected && connectionMonitorEnabled;
        _localMaxExpirationDisconnected = localMaxExpirationDisconnected;
        _defaultExpiration = defaultExpiration;
        _localIsStore = inner is NullSetCache;
    }

    private static IConnectionState GetConnectionMonitor(ISetCache inner, TimeSpan? period) =>
        inner is IConnectionState state
            ? new ConnectionStateMonitor(NullTelemetryProvider.Instance, period ?? TimeSpan.FromSeconds(5), state)
            : NullConnectionStateMonitor.Instance;

    public string Name => _name;

    public async ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (UseLocalOnly())
        {
            return LocalAdd(key, [item], LocalWriteExpiration(null, policy)) > 0;
        }
        var added = await _inner.AddAsync(cacheKey, item, policy, token).ConfigureAwait(false);
        LocalAddIfExists(key, [item]);
        return added;
    }

    public async ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        var materialized = Materialize(items);
        if (UseLocalOnly())
        {
            return LocalAdd(key, materialized, LocalWriteExpiration(null, policy));
        }
        var added = await _inner.AddAsync(cacheKey, materialized, policy, token).ConfigureAwait(false);
        LocalAddIfExists(key, materialized);
        return added;
    }

    public async ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        var materialized = Materialize(items);
        if (UseLocalOnly())
        {
            return LocalAdd(key, materialized, LocalWriteExpiration(FromTtl(expiration), policy));
        }
        var added = await _inner.AddAsync(cacheKey, materialized, expiration, policy, token).ConfigureAwait(false);
        LocalAddIfExists(key, materialized);
        return added;
    }

    public async ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        var materialized = Materialize(items);
        if (UseLocalOnly())
        {
            return LocalAdd(key, materialized, LocalWriteExpiration(expiration, policy));
        }
        var added = await _inner.AddAsync(cacheKey, materialized, expiration, policy, token).ConfigureAwait(false);
        LocalAddIfExists(key, materialized);
        return added;
    }

    public async ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (UseLocalOnly())
        {
            var popped = LocalPop<T>(key, 1);
            return popped.Count > 0 ? popped.First() : default;
        }
        var value = await _inner.PopAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        if (value is not null)
        {
            LocalRemove(key, [value]);
        }
        return value;
    }

    public async ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (UseLocalOnly())
        {
            return LocalPop<T>(key, count);
        }
        var values = await _inner.PopAsync<T>(cacheKey, count, policy, token).ConfigureAwait(false);
        if (values.Count > 0)
        {
            LocalRemove(key, values.Where(v => v is not null).Select(v => v!));
        }
        return values;
    }

    public async ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (TryGetSnapshot(key, out var snapshot))
        {
            if (LocalUsable())
            {
                return Deserialize<T>(snapshot.Members);
            }
            _memoryCache.Remove(key);
            return [];
        }
        var members = await _inner.MembersAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        if (members.Count > 0)
        {
            LocalReplace(key, members.Where(m => m is not null).Select(m => m!), LocalExpiration());
        }
        return members;
    }

    public async ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (TryGetSnapshot(key, out var snapshot))
        {
            if (LocalUsable())
            {
                return snapshot.Members.Contains(_serializer.Serialize(item));
            }
            _memoryCache.Remove(key);
            return false;
        }
        return await _inner.ContainsItemAsync(cacheKey, item, token).ConfigureAwait(false);
    }

    public async ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (TryGetSnapshot(key, out var snapshot))
        {
            if (LocalUsable())
            {
                return snapshot.Members.Count;
            }
            _memoryCache.Remove(key);
            return 0;
        }
        return await _inner.CountAsync<T>(cacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (TryGetSnapshot(key, out _))
        {
            if (LocalUsable())
            {
                return true;
            }
            _memoryCache.Remove(key);
            return false;
        }
        return await _inner.ContainsAsync<T>(cacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (UseLocalOnly())
        {
            return LocalRemove(key, [item]) > 0;
        }
        LocalRemove(key, [item]);
        return await _inner.RemoveItemAsync(cacheKey, item, token).ConfigureAwait(false);
    }

    public async ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        var materialized = Materialize(items);
        if (UseLocalOnly())
        {
            return LocalRemove(key, materialized);
        }
        LocalRemove(key, materialized);
        return await _inner.RemoveItemsAsync(cacheKey, materialized, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var key = Key(cacheKey, token);
        if (UseLocalOnly())
        {
            return LocalRemoveKey(key);
        }
        _memoryCache.Remove(key);
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

    private bool GetInnerCacheDisconnected() => _useLocalOnlyWhenDisconnected && !_connectionState.IsConnected;

    private bool UseLocalOnly() => _localIsStore || GetInnerCacheDisconnected();

    private bool LocalUsable() => _connectionState.IsConnected || _useLocalOnlyWhenDisconnected;

    private DateTimeOffset? LocalWriteExpiration(DateTimeOffset? requested, CachePolicy? policy)
    {
        requested ??= FromTtl(policy?.DistributedExpiration ?? (_localIsStore ? _defaultExpiration : null));
        return _localIsStore ? requested : DisconnectedExpiration(requested);
    }

    private object Gate(string key) => _gates.GetOrAdd(key, static _ => new object());

    private bool TryGetSnapshot(string key, [NotNullWhen(true)] out Snapshot? snapshot) =>
        _memoryCache.TryGetValue(key, out snapshot) && snapshot is not null;

    private void StoreSnapshot(string key, Snapshot snapshot)
    {
        if (snapshot.Members.IsEmpty)
        {
            _memoryCache.Remove(key);
            return;
        }
        var options = new MemoryCacheEntryOptions();
        if (snapshot.Expiration.HasValue)
        {
            options.SetAbsoluteExpiration(snapshot.Expiration.Value);
        }
        if (_trackSize)
        {
            options.SetSize(1);
        }
        _memoryCache.Set(key, snapshot, options);
    }

    private void LocalReplace<T>(string key, IEnumerable<T> members, DateTimeOffset? expiration)
    {
        var set = members.Select(m => _serializer.Serialize(m)).ToImmutableHashSet();
        lock (Gate(key))
        {
            StoreSnapshot(key, new Snapshot(set, expiration));
        }
    }

    private void LocalAddIfExists<T>(string key, IEnumerable<T> items)
    {
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return;
        }
        lock (Gate(key))
        {
            if (TryGetSnapshot(key, out var snapshot))
            {
                StoreSnapshot(key, snapshot with { Members = snapshot.Members.Union(values) });
            }
        }
    }

    private long LocalAdd<T>(string key, IEnumerable<T> items, DateTimeOffset? expiration)
    {
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }
        lock (Gate(key))
        {
            if (expiration.HasValue && expiration.Value <= DateTimeOffset.UtcNow)
            {
                _memoryCache.Remove(key);
                return 0;
            }
            var members = TryGetSnapshot(key, out var snapshot) ? snapshot.Members : ImmutableHashSet<RedisValue>.Empty;
            var updated = members.Union(values);
            StoreSnapshot(key, new Snapshot(updated, expiration));
            return updated.Count - members.Count;
        }
    }

    private long LocalRemove<T>(string key, IEnumerable<T> items)
    {
        var values = items.Select(i => _serializer.Serialize(i)).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }
        lock (Gate(key))
        {
            if (!TryGetSnapshot(key, out var snapshot))
            {
                return 0;
            }
            var updated = snapshot.Members.Except(values);
            StoreSnapshot(key, snapshot with { Members = updated });
            return snapshot.Members.Count - updated.Count;
        }
    }

    private bool LocalRemoveKey(string key)
    {
        lock (Gate(key))
        {
            if (!TryGetSnapshot(key, out _))
            {
                return false;
            }
            _memoryCache.Remove(key);
            return true;
        }
    }

    private IReadOnlyCollection<T?> LocalPop<T>(string key, long count)
    {
        if (count <= 0)
        {
            return [];
        }
        lock (Gate(key))
        {
            if (!TryGetSnapshot(key, out var snapshot) || snapshot.Members.IsEmpty)
            {
                return [];
            }
            var pool = snapshot.Members.ToArray();
            var take = (int)Math.Min(count, pool.Length);
            var picked = new RedisValue[take];
            for (var i = 0; i < take; i++)
            {
                var j = Random.Shared.Next(i, pool.Length);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                picked[i] = pool[i];
            }
            StoreSnapshot(key, snapshot with { Members = snapshot.Members.Except(picked) });
            return Deserialize<T>(picked);
        }
    }

    private IReadOnlyCollection<T?> Deserialize<T>(IReadOnlyCollection<RedisValue> values)
    {
        if (values.Count == 0)
        {
            return [];
        }
        var list = new List<T?>(values.Count);
        foreach (var value in values)
        {
            if (value.IsNull)
            {
                continue;
            }
            list.Add(_serializer.Deserialize<T>(value));
        }
        return list;
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
