namespace UiPath.Platform.Caching;

/// <summary>
/// Creates set caches, mirroring <see cref="ICacheFactory"/> for Redis sets. Lives in the
/// <c>UiPath.Platform.Caching.Queue</c> package (set support is opt-in); register it via
/// <c>AddRedisSetCache</c>. Inject this instead of <see cref="ICacheFactory"/> when you need sets.
/// </summary>
public interface IQueueCacheFactory
{
    ISetCache CreateSetCache();
}
