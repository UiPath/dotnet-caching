namespace UiPath.Platform.Caching;

public interface ICacheFactory : IDisposable
{
    ICache CreateCache(string? providerName = null, Type? entityType = null, Type? callerType = null);

    IHashCache CreateHashCache(string? providerName = null, Type? entityType = null, Type? callerType = null);

    void AddProvider(ICacheProvider provider);
}
