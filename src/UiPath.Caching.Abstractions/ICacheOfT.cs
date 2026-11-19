#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;
public partial interface ICache<T>
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);
}
