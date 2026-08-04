namespace UiPath.Caching;

/// <summary>
/// Creates the queue-package caches (sets today), mirroring <see cref="ICacheFactory"/>. Lives in
/// the <c>UiPath.Caching.Queue</c> package (queue support is opt-in); register it via
/// <c>AddQueueMemory</c> / <c>AddQueueRedis</c> / <c>AddQueueInMemoryRedis</c>. Inject this instead
/// of <see cref="ICacheFactory"/> when you need sets.
/// </summary>
public interface IQueueCacheFactory : IDisposable
{
    /// <summary>Names of the registered, enabled queue-cache providers.</summary>
    IEnumerable<string> ProviderNames { get; }

    /// <summary>
    /// Returns the <see cref="ISetCache"/> for the given provider (see
    /// <see cref="KnownCacheProviderNames"/>). When <paramref name="providerName"/> is
    /// <see langword="null"/> or not registered, the <see cref="CacheOptions.DefaultCache"/>
    /// provider is used, degrading to <see cref="NullSetCache"/> when that is missing too.
    /// </summary>
    ISetCache CreateSetCache(string? providerName = null);

    /// <summary>Registers (or replaces) a provider under its <see cref="IQueueCacheProvider.Name"/>.</summary>
    void AddProvider(IQueueCacheProvider provider);
}
