namespace UiPath.Platform.Caching.Redis;

public class NullCache : IRedisCache, IHybridCache
{
    public static readonly NullCache Instance = new NullCache();

    public string? InstanceName => default;

    public Task<bool> ContainsAsync(string key, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default) =>
        Task.FromResult(default(DateTimeOffset?));

    public Task<T?> GetAsync<T>(string key, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task RefreshAsync<T>(string key, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.CompletedTask;

    public Task RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.CompletedTask;

    public Task<bool> RemoveAsync<T>(string key, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(string key, T? value, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default) =>
        Task.FromResult(default(TimeSpan?));
}
