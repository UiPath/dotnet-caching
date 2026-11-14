namespace UiPath.Caching;

/// <summary>The hash form of <see cref="IAdoptionReadCache"/>.</summary>
internal interface IAdoptionReadHashCache
{
    ValueTask<ICacheEntry<IDictionary<string, T?>>> ReadForAdoptionAsync<T>(CacheKey cacheKey, CancellationToken token);
}
