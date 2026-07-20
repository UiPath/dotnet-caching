namespace UiPath.Caching;

/// <summary>
/// Creates set caches, mirroring <see cref="ICacheFactory"/>. Lives in the
/// <c>UiPath.Caching.Queue</c> package (set support is opt-in); register it via
/// <c>AddInMemorySetCache</c> / <c>AddRedisSetCache</c> / <c>AddInMemoryRedisSetCache</c>. Inject
/// this instead of <see cref="ICacheFactory"/> when you need sets.
/// </summary>
public interface IQueueCacheFactory : IDisposable
{
    /// <summary>Names of the registered, enabled set-cache providers.</summary>
    IEnumerable<string> ProviderNames { get; }

    /// <summary>Returns the <see cref="ISetCache"/> for the default provider.</summary>
    ISetCache CreateSetCache();

    /// <summary>
    /// Returns the <see cref="ISetCache"/> for the given provider (see
    /// <see cref="KnownCacheProviderNames"/>). When <paramref name="providerName"/> is
    /// <see langword="null"/> or not registered, the <see cref="CacheOptions.DefaultCache"/>
    /// provider is used, degrading to <see cref="NullSetCache"/> when that is missing too.
    /// </summary>
    ISetCache CreateSetCache(string? providerName);

    /// <summary>Registers (or replaces) a provider under its <see cref="ISetCacheProvider.Name"/>.</summary>
    void AddProvider(ISetCacheProvider provider);
}
