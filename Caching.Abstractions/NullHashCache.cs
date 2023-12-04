using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullHashCache : IHashCache
{
    public static readonly NullHashCache Instance = new();

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(false);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(default(DateTimeOffset?));

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(NullCacheEntry<IDictionary<string, T?>>.Instance);

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult<IDictionary<string, string?>?>(default);

    public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default) =>
        ValueTask.FromResult(default(T?));

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default) =>
        ValueTask.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(default(TimeSpan?));

    public void Dispose()
    {
        // nothing to dispose
    }

    private sealed record NullCacheEntry<T> : ICacheEntry<T>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint.Rule", "S3218:Inner class members should not shadow outer class names")]
        public static readonly ICacheEntry<T> Instance = new NullCacheEntry<T>();

        public T? Value => default;

        public DateTimeOffset Expiration => DateTimeOffset.MinValue;

        public IDictionary<string, string?>? Metadata => default;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            NullCacheEntry<T>.Instance;
    }
}
