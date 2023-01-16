namespace UiPath.Platform.Caching;

public interface IRegionCache<T>
    where T : class
{
    Task<T?> GetItemAsync(Region region, string key, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync(Region region, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync(Region region, string[] keys, CancellationToken token = default);

    Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Region region, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(Region region, IDictionary<string, T?> values, CancellationToken token = default);

    Task<bool> SetAsync(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RefreshAsync(Region region, CancellationToken token = default);

    Task<bool> RefreshAsync(Region region, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync(Region region, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync(Region region, RegionCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RemoveAsync(Region region, CancellationToken token = default);

    Task<bool> ContainsAsync(Region region, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default);

    Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default);

    Task<bool> SetExtendedPropertiesAsync(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default);
}
