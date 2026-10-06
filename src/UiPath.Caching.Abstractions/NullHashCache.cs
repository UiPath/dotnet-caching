using System.Collections.Immutable;

namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullHashCache : IHashCache, ISpanKeyHashCache
{
    public static readonly NullHashCache Instance = new();

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

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(NullCacheEntry<IDictionary<string, T?>>.Instance);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult<IDictionary<string, string?>?>(default);
    }

    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(TimeSpan?));
    }

    ValueTask<T?> ISpanKeyHashCache.GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token)
        where T : default =>
        GetItemAsync<T>(CacheKey.Null, field, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token)
        where T : default =>
        GetAsync<T>(CacheKey.Null, policy, token);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> ISpanKeyHashCache.GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token)
        where T : default =>
        GetCacheEntryAsync<T>(CacheKey.Null, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token)
        where T : default =>
        ReturnGeneratorAsync(generator, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token)
        where T : default =>
        ReturnGeneratorAsync(generator, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
        where T : default =>
        ReturnGeneratorAsync(generator, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token)
        where T : default =>
        ReturnGeneratorAsync(generator, token);

    ValueTask<bool> ISpanKeyHashCache.ContainsAsync<T>(Span<char> cacheKey, CancellationToken token) =>
        ContainsAsync<T>(CacheKey.Null, token);

    public void Dispose()
    {
        // nothing to dispose
    }

    private static ValueTask<bool> ReturnTrueAsync<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(true);
    }

    private static async ValueTask<IDictionary<string, T?>> ReturnGeneratorAsync<T>(Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await generator(token).ConfigureAwait(false);
    }

    private sealed record NullCacheEntry<T> : ICacheEntry<T>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint.Rule", "S3218:Inner class members should not shadow outer class names")]
        public static readonly ICacheEntry<T> Instance = new NullCacheEntry<T>();

        public T? Value => default;

        public DateTimeOffset Expiration => DateTimeOffset.MinValue;

        public IDictionary<string, string?>? Metadata => default;

        object? ICacheEntry.Value => default;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            NullCacheEntry<T>.Instance;
    }
}
