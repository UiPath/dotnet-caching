namespace UiPath.Platform.Caching;

public interface ICache
{
    string? InstanceName { get; }

    Task<T?> GetAsync<T>(string key, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RemoveAsync<T>(string key, CancellationToken token = default);

    Task<bool> SetAsync<T>(string key, T? value, CancellationToken token = default);

    Task<bool> SetAsync<T>(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task RefreshAsync<T>(string key, CancellationToken token = default);

    Task RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default);

    Task RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> ContainsAsync(string key, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default);
}
