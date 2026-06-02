namespace UiPath.Platform.Caching;

public interface ICacheFactory : IDisposable
{
    IEnumerable<string> ProviderNames { get; }

    ICachePolicyFactory? PolicyFactory => null;

    ICache CreateCache(string? providerName = null);

    IHashCache CreateHashCache(string? providerName = null);

    void AddProvider(ICacheProvider provider);
}
