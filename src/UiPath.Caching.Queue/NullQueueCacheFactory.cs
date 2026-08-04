namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullQueueCacheFactory : IQueueCacheFactory
{
    public static readonly NullQueueCacheFactory Instance = new();

    public IEnumerable<string> ProviderNames => [];

    public void AddProvider(IQueueCacheProvider provider)
    {
        // do nothing
    }

    public ISetCache CreateSetCache(string? providerName = null) =>
        NullSetCache.Instance;

    public void Dispose()
    {
        // do nothing
    }
}
