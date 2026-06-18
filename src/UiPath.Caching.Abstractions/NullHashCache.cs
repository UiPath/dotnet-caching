using System.Collections.Immutable;

namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullHashCache : IHashCache
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

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(NullCacheEntry<IDictionary<string, T?>>.Instance);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult<IDictionary<string, string?>?>(default);
    }

    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy = null, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CachePolicy? policy = null, CancellationToken token = default) =>
        ReturnGeneratorAsync(generator, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) => ReturnTrueAsync<T>();

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(TimeSpan?));
    }

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

        object? ICacheEntry.Value => default;

        public DateTimeOffset Expiration => DateTimeOffset.MinValue;

        public IDictionary<string, string?>? Metadata => default;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            NullCacheEntry<T>.Instance;
    }
}
