namespace UiPath.Caching;

/// <summary>
/// Default <see cref="IQueueCacheFactory"/>. Mirrors <see cref="CacheFactory"/>: holds the
/// registered <see cref="ISetCacheProvider"/> instances keyed by name and hands out the matching
/// <see cref="ISetCache"/>, defaulting to <see cref="CacheOptions.DefaultCache"/> and degrading to
/// <see cref="NullSetCache"/> when no provider matches.
/// </summary>
public sealed class QueueCacheFactory : IQueueCacheFactory
{
    private readonly Dictionary<string, ISetCacheProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CacheOptions _options;
    private volatile bool _disposed;

    /// <summary>
    /// Single-cache factory: hands out <paramref name="setCache"/> for every request. Retained for
    /// backward compatibility; prefer the provider-based constructors.
    /// </summary>
    public QueueCacheFactory(ISetCache setCache)
    {
        ArgumentNullException.ThrowIfNull(setCache);
        _options = new CacheOptions { DefaultCache = setCache.Name };
        AddProvider(new SingleSetCacheProvider(setCache));
    }

    public QueueCacheFactory(IOptions<CacheOptions> cacheOptions)
        : this(cacheOptions, [])
    {
    }

    public QueueCacheFactory(IOptions<CacheOptions> cacheOptions, IEnumerable<ISetCacheProvider> providers)
    {
        _options = cacheOptions.Value;
        foreach (var provider in providers)
        {
            AddProvider(provider);
        }
    }

    public IEnumerable<string> ProviderNames => _providers.Values.Where(p => p.Enabled).Select(p => p.Name);

    public void AddProvider(ISetCacheProvider provider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _providers[provider.Name] = provider;
    }

    public ISetCache CreateSetCache() => CreateSetCache(providerName: null);

    public ISetCache CreateSetCache(string? providerName) =>
        GetProvider(providerName ?? _options.DefaultCache)?.CreateSetCache() ?? DefaultSetCache();

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try
            {
                p.Value.Dispose();
            }
            catch
            {
                // Swallow exceptions on dispose
            }
        }
        _providers.Clear();
        _disposed = true;
    }

    private ISetCache DefaultSetCache() => GetProvider(_options.DefaultCache)?.CreateSetCache() ?? NullSetCache.Instance;

    private ISetCacheProvider? GetProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) && provider.Enabled ? provider : null;

    // Adapts the single-cache constructor to the provider model: that constructor points
    // CacheOptions.DefaultCache at this provider, so every request resolves to the supplied cache.
    private sealed class SingleSetCacheProvider : ISetCacheProvider
    {
        private readonly ISetCache _setCache;

        public SingleSetCacheProvider(ISetCache setCache) => _setCache = setCache;

        public string Name => _setCache.Name;

        public bool Enabled => true;

        public ISetCache CreateSetCache() => _setCache;

        public void Dispose()
        {
            // The factory does not own the externally supplied cache.
        }
    }
}
