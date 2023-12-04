using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullCache : ICache
{
    public static readonly NullCache Instance = new();

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(false);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(default(DateTimeOffset?));

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(default(T?));

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) =>
        ValueTask.FromResult(default(T?));

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(default(T?));

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(default(T?));

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        ValueTask.FromResult(default(TimeSpan?));

    public void Dispose()
    {
        // Nothing to dispose
    }
}
