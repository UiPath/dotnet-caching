namespace UiPath.Platform.Caching;

public class Cache<T> : ICache<T>
    where T : class
{
    private readonly ICache _cache;

    public Cache(ICache cache) =>
        _cache = cache;

    public Task<bool> ContainsAsync(string key, CancellationToken token = default) =>
        _cache.ContainsAsync(key, token);

    public Task<T?> GetAsync(string key, CancellationToken token = default) =>
        _cache.GetAsync<T>(key, token);

    public Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(key, generator, token);

    public Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(key, generator, expiration, token);

    public Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(key, generator, expiration, token);

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(key, token);

    public Task RefreshAsync(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(key, expiration, token);

    public Task RefreshAsync(string key, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(key, expiration, token);

    public Task<bool> RemoveAsync(string key, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(key, token);

    public Task<bool> SetAsync(string key, T? value, CancellationToken token = default) =>
        _cache.SetAsync(key, value, token);

    public Task<bool> SetAsync(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(key, value, expiration, token);

    public Task<bool> SetAsync(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(key, value, expiration, token);

    public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default) =>
        _cache.TimeToLiveAsync(key, token);

    public Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default) =>
        _cache.ExpireTimeAsync(key, token);
}
