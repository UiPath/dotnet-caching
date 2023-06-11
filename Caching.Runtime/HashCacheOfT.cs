namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class HashCache<T> : IHashCache<T>
    where T : class
{
    private readonly IHashCache _cache;

    public HashCache(ICacheFactory cacheFactory, IOptions<CacheOptions> cacheOptions) => 
        _cache = cacheFactory.CreateHashCache(providerName: cacheOptions.Value.DefaultCache, entityType: typeof(T), callerType: GetType());

    public HashCache(IHashCache cache) =>
        _cache = cache;

    public Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(cacheKey, token);

    public Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(cacheKey, token);

    public Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(cacheKey, token);

    public Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.GetAsync<T>(cacheKey, fields, token);

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetCacheEntryAsync<T>(cacheKey, token);

    public Task<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetMetadataAsync<T>(cacheKey, token);

    public Task<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default) =>
        _cache.GetItemAsync<T>(cacheKey, field, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, expiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, options, token);

    public Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(cacheKey, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, values, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, values, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, values, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, values, options, token);

    public Task<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        _cache.SetMetadataAsync<T>(cacheKey, metadata, token);

    public Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(cacheKey, token);
}
