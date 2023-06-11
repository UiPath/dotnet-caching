using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullCache : ICache
{
    public static readonly NullCache Instance = new();

    public Task<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(default(DateTimeOffset?));

    public Task<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(true);

    public Task<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        Task.FromResult(default(TimeSpan?));

    public void Dispose()
    {
        // Nothing to dispose
    }
}
