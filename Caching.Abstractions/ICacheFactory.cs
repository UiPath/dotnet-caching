namespace UiPath.Platform.Caching;

public interface ICacheFactory : IDisposable
{
    ICache CreateCache(string? providerName = null);

    IHashCache CreateHashCache(string? providerName = null);

    void AddProvider(ICacheProvider provider);
}
