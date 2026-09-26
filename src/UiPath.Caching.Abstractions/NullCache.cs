namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullCache : ICache
{
    public static readonly NullCache Instance = new();

    public string Name => "Null";

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(false);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(DateTimeOffset?));
    }

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(cacheKeys.Select(k => new KeyValuePair<CacheKey, T?>(k, default(T?))).ToArray());
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(NullCacheEntry<T?>.Instance);
    }

    public ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(cacheKeys
            .Select(k => new KeyValuePair<CacheKey, ICacheEntry<T?>>(k, NullCacheEntry<T?>.Instance))
            .ToArray());
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => ReturnGeneratorAsync(entries, generator, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => ReturnGeneratorAsync(entries, generator, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => ReturnGeneratorAsync(entries, generator, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    /// <summary>
    /// Always <c>true</c>, as <c>SetAsync</c> is here: nothing is retained, so no key pre-exists and
    /// nobody loses. No exclusion at all, then — and this type is what
    /// <c>ICacheFactory.CreateCache</c> resolves to for an absent or disabled provider, so assert the
    /// provider you expect at startup.
    /// </summary>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(TimeSpan?));
    }

    public void Dispose()
    {
        // Nothing to dispose
    }

    private static ValueTask<bool> ReturnTrueAsync<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(true);
    }

    private static async ValueTask<T?> ReturnGeneratorAsync<T>(Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await generator(token).ConfigureAwait(false);
    }

    /// <summary>What <c>BatchGetOrAdd</c> returns over a cache that never hits: the generator answers each key's first state, states on the same key share that answer, and a key it skips gets the default.</summary>
    private static async ValueTask<KeyValuePair<TState, T?>[]> ReturnGeneratorAsync<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        NotCacheableException.ThrowIfNotCacheable<T>();

        var distinct = entries.DistinctBy(e => e.Value).ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }
        var requests = distinct.DistinctBy(e => e.Key).ToArray();
        var keyOfRequest = requests.ToDictionary(e => e.Value, e => e.Key);
        var answers = (await generator(Array.ConvertAll(requests, e => e.Value), token).ConfigureAwait(false) ?? [])
            .Where(a => keyOfRequest.ContainsKey(a.Key))
            .DistinctBy(a => a.Key)
            .ToDictionary(a => keyOfRequest[a.Key], a => a.Value);
        return Array.ConvertAll(distinct, e => new KeyValuePair<TState, T?>(e.Value, answers.GetValueOrDefault(e.Key)));
    }

    private sealed record NullCacheEntry<T> : ICacheEntry<T>
    {
        [SuppressMessage("SonarLint.Rule", "S3218:Inner class members should not shadow outer class names")]
        public static readonly ICacheEntry<T> Instance = new NullCacheEntry<T>();

        public T? Value => default;

        public DateTimeOffset Expiration => DateTimeOffset.MinValue;

        public IDictionary<string, string?>? Metadata => default;

        object? ICacheEntry.Value => default;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            NullCacheEntry<T>.Instance;
    }
}
