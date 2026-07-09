namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullQueueCacheFactory : IQueueCacheFactory
{
    public static readonly NullQueueCacheFactory Instance = new();

    public ISetCache CreateSetCache() => NullSetCache.Instance;
}
