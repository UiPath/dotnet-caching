namespace UiPath.Caching.Tests.Fakes;

/// <summary>Minimal in-memory <see cref="ICache"/> exercising the batch <c>GetOrAddAsync</c> default interface methods.</summary>
internal sealed class DictionaryCache : ICache
{
    private readonly Dictionary<CacheKey, object?> _store = [];

    public string Name => "dictionary";

    public bool CacheNullValues { get; set; } = true;

    public int GetCacheEntriesCalls { get; private set; }

    public int SetCalls { get; private set; }

    public int TryAddCalls { get; private set; }

    public List<CacheKey[]> SetKeySets { get; } = [];

    public void Seed<T>(CacheKey key, T? value) => _store[key] = value;

    public bool Contains(CacheKey key) => _store.ContainsKey(key);

    public T? Read<T>(CacheKey key) => _store.TryGetValue(key, out var v) ? (T?)v : default;

    public ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(
        CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default)
    {
        GetCacheEntriesCalls++;
        var results = cacheKeys
            .Select(k => new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                k,
                _store.TryGetValue(k, out var v)
                    ? new TestCacheEntry<T?> { Value = (T?)v, Expiration = DateTimeOffset.MaxValue, Found = true }
                    : new TestCacheEntry<T?> { Value = default, Expiration = DateTimeOffset.MinValue }))
            .ToArray();
        return ValueTask.FromResult(results);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy = null, CancellationToken token = default)
    {
        SetCalls++;
        SetKeySets.Add(keyValues.Select(kv => kv.Key).ToArray());
        foreach (var kv in keyValues)
        {
            if (kv.Value is null && !CacheNullValues)
            {
                _store.Remove(kv.Key);
                continue;
            }
            _store[kv.Key] = kv.Value;
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default) =>
        ValueTask.FromResult<ICacheEntry<T?>>(_store.TryGetValue(cacheKey, out var v)
            ? new TestCacheEntry<T?> { Value = (T?)v, Expiration = DateTimeOffset.MaxValue, Found = true }
            : new TestCacheEntry<T?> { Value = default, Expiration = DateTimeOffset.MinValue });

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default) =>
        ValueTask.FromResult(Read<T>(cacheKey));

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default) =>
        ValueTask.FromResult(cacheKeys.Select(k => new KeyValuePair<CacheKey, T?>(k, Read<T>(k))).ToArray());

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default) =>
        throw new NotSupportedException();

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        throw new NotSupportedException();

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        throw new NotSupportedException();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy = null, CancellationToken token = default) =>
        SetAsync([new KeyValuePair<CacheKey, T?>(cacheKey, value)], policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        SetAsync([new KeyValuePair<CacheKey, T?>(cacheKey, value)], policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        SetAsync([new KeyValuePair<CacheKey, T?>(cacheKey, value)], policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        SetAsync(keyValues, policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        SetAsync(keyValues, policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy = null, CancellationToken token = default)
    {
        TryAddCalls++;
        if (value is null && !CacheNullValues)
        {
            return ValueTask.FromResult(false);
        }
        return ValueTask.FromResult(_store.TryAdd(cacheKey, value));
    }

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        TryAddAsync(cacheKey, value, policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        TryAddAsync(cacheKey, value, policy, token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(_store.Remove(cacheKey));

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        foreach (var k in cacheKey) { _store.Remove(k); }
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default) => ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ValueTask.FromResult(true);

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ValueTask.FromResult(_store.ContainsKey(cacheKey));

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ValueTask.FromResult<TimeSpan?>(null);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ValueTask.FromResult<DateTimeOffset?>(null);

    public void Dispose()
    {
    }
}
