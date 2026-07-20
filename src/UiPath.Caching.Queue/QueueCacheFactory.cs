namespace UiPath.Caching;

/// <summary>
/// Default <see cref="IQueueCacheFactory"/>. Mirrors <c>CacheFactory</c>: holds the registered
/// <see cref="ISetCacheProvider"/> instances keyed by name and hands out the matching
/// <see cref="ISetCache"/>, defaulting to <see cref="CacheOptions.DefaultCache"/>.
/// </summary>
public sealed class QueueCacheFactory : IQueueCacheFactory
{
    private readonly Dictionary<string, ISetCacheProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISetCache? _single;
    private readonly string _defaultCache = KnownCacheProviderNames.InMemoryRedis;

    /// <summary>
    /// Single-provider factory: always hands out <paramref name="setCache"/> regardless of the
    /// requested provider name. Retained for backward compatibility.
    /// </summary>
    public QueueCacheFactory(ISetCache setCache)
    {
        ArgumentNullException.ThrowIfNull(setCache);
        _single = setCache;
    }

    /// <summary>
    /// Provider-based factory: selects an <see cref="ISetCacheProvider"/> by name (or by
    /// <see cref="CacheOptions.DefaultCache"/> when no name is given).
    /// </summary>
    public QueueCacheFactory(IEnumerable<ISetCacheProvider> providers, IOptions<CacheOptions> cacheOptions)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _defaultCache = cacheOptions?.Value.DefaultCache ?? KnownCacheProviderNames.InMemoryRedis;
        foreach (var provider in providers)
        {
            _providers[provider.Name] = provider;
        }
    }

    public IEnumerable<string> ProviderNames =>
        _single is not null ? [_single.Name] : _providers.Values.Where(p => p.Enabled).Select(p => p.Name);

    public ISetCache CreateSetCache() => Resolve(_defaultCache, allowFallback: true);

    // A null providerName means "the default provider" and is allowed to fall back to the sole
    // registered provider. An explicit name must resolve exactly (or yield NullSetCache) — this is
    // what the multilayer provider uses to fetch its Redis tier, and falling back there could
    // resolve the multilayer provider back to itself.
    public ISetCache CreateSetCache(string? providerName) =>
        providerName is null ? Resolve(_defaultCache, allowFallback: true) : Resolve(providerName, allowFallback: false);

    private ISetCache Resolve(string providerName, bool allowFallback)
    {
        if (_single is not null)
        {
            return _single;
        }
        var provider = GetProvider(providerName);
        if (provider is not null)
        {
            return provider.CreateSetCache();
        }
        return allowFallback && FallbackProvider() is { } sole ? sole.CreateSetCache() : NullSetCache.Instance;
    }

    private ISetCacheProvider? GetProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) && provider.Enabled ? provider : null;

    // When the default provider is not registered, fall back to the sole enabled provider (if
    // unambiguous) so a single AddXxxSetCache registration "just works" without configuring
    // CacheOptions.DefaultCache.
    private ISetCacheProvider? FallbackProvider()
    {
        var enabled = _providers.Values.Where(p => p.Enabled).ToArray();
        return enabled.Length == 1 ? enabled[0] : null;
    }
}
