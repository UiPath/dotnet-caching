using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullCacheFactory : ICacheFactory
{
    public static readonly NullCacheFactory Instance = new();

    public void AddProvider(ICacheProvider provider)
    {
        // do nothing
    }

    public ICache CreateCache(string? providerName = null, Type? entityType = null, Type? callerType = null) =>
        NullCache.Instance;

    public IHashCache CreateHashCache(string? providerName = null, Type? entityType = null, Type? callerType = null) =>
        NullHashCache.Instance;

    public void Dispose()
    {
        // do nothing
    }
}
