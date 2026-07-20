namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullQueueCacheFactory : IQueueCacheFactory
{
    public static readonly NullQueueCacheFactory Instance = new();

    public IEnumerable<string> ProviderNames => [];

    public void AddProvider(ISetCacheProvider provider)
    {
        // do nothing
    }

    public ISetCache CreateSetCache() => NullSetCache.Instance;

    public ISetCache CreateSetCache(string? providerName) => NullSetCache.Instance;

    public void Dispose()
    {
        // do nothing
    }
}
