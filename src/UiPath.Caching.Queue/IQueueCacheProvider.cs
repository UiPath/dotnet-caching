namespace UiPath.Caching;

/// <summary>
/// Provides the queue-package caches for one backing (InMemory, Redis or InMemoryRedis), mirroring
/// <see cref="ICacheProvider"/> for the normal/hash caches: one provider per backing, one create
/// method per collection kind (sets today; further kinds such as lists are added here). Registered
/// providers are selected by name through <see cref="IQueueCacheFactory"/>.
/// </summary>
public interface IQueueCacheProvider : IDisposable
{
    /// <summary>The provider name, e.g. one of <see cref="KnownCacheProviderNames"/>.</summary>
    string Name { get; }

    /// <summary>Whether this provider is enabled and eligible for selection.</summary>
    bool Enabled { get; }

    /// <summary>Returns the (typically cached) <see cref="ISetCache"/> for this provider.</summary>
    ISetCache CreateSetCache();
}
