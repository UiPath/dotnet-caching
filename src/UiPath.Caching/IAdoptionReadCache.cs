namespace UiPath.Caching;

/// <summary>A cache whose read for adoption takes the value and its remaining lifetime as one snapshot and fails by throwing, where a plain read answers a miss.</summary>
internal interface IAdoptionReadCache
{
    ValueTask<ICacheEntry<T?>> ReadForAdoptionAsync<T>(CacheKey cacheKey, CancellationToken token);
}
