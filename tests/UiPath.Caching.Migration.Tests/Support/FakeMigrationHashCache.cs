namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>The library hash cache as a script: a read answers fields, a <see cref="SnapshotOutcome"/> or nothing (absent), and a write answers a <see cref="StoreOutcome"/>.</summary>
internal sealed class FakeMigrationHashCache(TimeProvider? clock = null) : IMigrationHashCache
{
    public Func<CacheKey, CancellationToken, Task<object?>> Read { get; set; } = (_, _) => Task.FromResult<object?>(null);

    public Func<CacheKey, CancellationToken, Task<StoreOutcome>> Write { get; set; } = (_, _) => Task.FromResult(StoreOutcome.Committed);

    public List<CacheKey> Keys { get; } = [];

    public List<(string Call, HashCacheEntryOptions? Options)> Calls { get; } = [];

    private DateTimeOffset Expiration => clock?.ToDateTimeOffset(TimeSpan.FromMinutes(1)) ?? DateTimeOffset.MaxValue;

    public async ValueTask<HashSnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        return await Read(cacheKey, token) switch
        {
            IDictionary<string, T?> fields => new HashSnapshotRead<T>(SnapshotOutcome.Found, (ICacheEntry<IDictionary<string, T?>>)(object)new TestEntry<IDictionary<string, T?>>(fields, Expiration)),
            SnapshotOutcome outcome => new HashSnapshotRead<T>(outcome, null),
            _ => new HashSnapshotRead<T>(SnapshotOutcome.Absent, null),
        };
    }

    public async ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        Calls.Add(("write", options));
        return await Write(cacheKey, token);
    }

    public async ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        Calls.Add(("delete", null));
        return await Write(cacheKey, token);
    }

    public async ValueTask<StoreOutcome> DeleteFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        Calls.Add(("fields", null));
        return await Write(cacheKey, token);
    }

    public async ValueTask<StoreOutcome> RenewExpiryAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        Calls.Add(("expiry", options));
        return await Write(cacheKey, token);
    }

    public async ValueTask<StoreOutcome> RenewAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        Keys.Add(cacheKey);
        Calls.Add(("renew", options));
        return await Write(cacheKey, token);
    }
}
