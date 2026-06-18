namespace UiPath.Caching;

/// <summary>
/// Default <see cref="IQueueCacheFactory"/>. Sets have a single Redis backing, so it simply hands out
/// the <see cref="ISetCache"/> registered by <c>AddRedisSetCache</c> (analogous to how
/// <c>CacheFactory.CreateCache()</c> returns a provider's cache).
/// </summary>
public sealed class QueueCacheFactory : IQueueCacheFactory
{
    private readonly ISetCache _setCache;

    public QueueCacheFactory(ISetCache setCache)
    {
        ArgumentNullException.ThrowIfNull(setCache);
        _setCache = setCache;
    }

    public ISetCache CreateSetCache() => _setCache;
}
