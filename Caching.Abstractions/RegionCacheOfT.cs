namespace UiPath.Platform.Caching;

public class RegionCache<T> : IRegionCache<T>
    where T : class
{
    private readonly IRegionCache _cache;

    public RegionCache(IRegionCache cache) =>
        _cache = cache;

    public Task<bool> ContainsAsync(Region region, CancellationToken token = default) =>
        _cache.ContainsAsync(region, token);

    public Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default) =>
        _cache.ExpireTimeAsync(region, token);

    public Task<IDictionary<string, T?>> GetAsync(Region region, CancellationToken token = default) =>
        _cache.GetAsync<T>(region, token);

    public Task<IDictionary<string, T?>> GetAsync(Region region, string[] keys, CancellationToken token = default) =>
        _cache.GetAsync<T>(region, keys, token);

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Region region, CancellationToken token = default) =>
        _cache.GetCacheEntryAsync<T>(region, token);

    public Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default) =>
        _cache.GetExtendedPropertiesAsync(region, token);

    public Task<T?> GetItemAsync(Region region, string key, CancellationToken token = default) =>
        _cache.GetItemAsync<T>(region, key, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(region, generator, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(region, generator, expiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(region, generator, expiration, token);

    public Task<bool> RefreshAsync(Region region, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(region, token);

    public Task<bool> RefreshAsync(Region region, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(region, expiration, token);

    public Task<bool> RefreshAsync(Region region, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(region, expiration, token);

    public Task<bool> RefreshAsync(Region region, RegionCacheEntryOptions options, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(region, options, token);

    public Task<bool> RemoveAsync(Region region, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(region, token);

    public Task<bool> SetAsync(Region region, IDictionary<string, T?> values, CancellationToken token = default) =>
        _cache.SetAsync(region, values, token);

    public Task<bool> SetAsync(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(region, values, expiration, token);

    public Task<bool> SetAsync(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(region, values, expiration, token);

    public Task<bool> SetAsync(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default) =>
        _cache.SetAsync(region, values, options, token);

    public Task<bool> SetExtendedPropertiesAsync(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default) =>
        _cache.SetExtendedPropertiesAsync<T>(region, extendedProperties, token);

    public Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default) =>
        _cache.TimeToLiveAsync(region, token);
}
