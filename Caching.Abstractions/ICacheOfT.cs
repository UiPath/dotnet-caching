namespace UiPath.Platform.Caching;
public interface ICache<T>
    where T : class
{
    Task<T?> GetAsync(string key, CancellationToken token = default);

    Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, CancellationToken token = default);

    Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<T?> GetOrAddAsync(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RemoveAsync(string key, CancellationToken token = default);

    Task<bool> SetAsync(string key, T? value, CancellationToken token = default);

    Task<bool> SetAsync(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task RefreshAsync(string key, CancellationToken token = default);

    Task RefreshAsync(string key, TimeSpan? expiration = null, CancellationToken token = default);

    Task RefreshAsync(string key, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> ContainsAsync(string key, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default);
}
