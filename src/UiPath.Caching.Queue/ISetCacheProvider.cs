namespace UiPath.Caching;

/// <summary>
/// Creates <see cref="ISetCache"/> instances for a specific backing (InMemory, Redis or
/// InMemoryRedis), mirroring <see cref="ICacheProvider"/> for the normal/hash caches. Registered
/// providers are selected by name through <see cref="IQueueCacheFactory"/>.
/// </summary>
public interface ISetCacheProvider : IDisposable
{
    /// <summary>The provider name, e.g. one of <see cref="KnownCacheProviderNames"/>.</summary>
    string Name { get; }

    /// <summary>Whether this provider is enabled and eligible for selection.</summary>
    bool Enabled { get; }

    /// <summary>Returns the (typically cached) <see cref="ISetCache"/> for this provider.</summary>
    ISetCache CreateSetCache();
}
