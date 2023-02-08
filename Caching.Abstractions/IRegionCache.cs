namespace UiPath.Platform.Caching;

public interface IRegionCache
{
    string? InstanceName { get; }

    Task<T?> GetItemAsync<T>(Region region, string key, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync<T>(Region region, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync<T>(Region region, string[] keys, CancellationToken token = default);

    Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Region region, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default);

    Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, CancellationToken token = default);

    Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(Region region, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(Region region, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(Region region, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(Region region, RegionCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RemoveAsync<T>(Region region, CancellationToken token = default);

    Task<bool> ContainsAsync(Region region, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default);

    Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default);

    Task<bool> SetExtendedPropertiesAsync<T>(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default);
}
