namespace UiPath.Platform.Caching;

public sealed class CacheFactory : ICacheFactory
{
    private readonly IDictionary<string, ICacheProvider> _providers = new Dictionary<string, ICacheProvider>(StringComparer.OrdinalIgnoreCase);
    private readonly CacheOptions _options;
    private volatile bool _disposed;

    public CacheFactory(IOptions<CacheOptions> cacheOptions)
        : this(cacheOptions, Array.Empty<ICacheProvider>())
    {
    }

    public CacheFactory(IOptions<CacheOptions> cacheOptions, IEnumerable<ICacheProvider> providers)
    {
        _options = cacheOptions.Value;
        foreach (var provider in providers)
        {
            AddProvider(provider);
        }
    }

    public void AddProvider(ICacheProvider provider)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CacheFactory));
        }

        if (!provider.Enabled)
        {
            return;
        }

        _providers[provider.Name] = provider;
    }

    public ICache CreateCache(string? providerName = null, Type? entityType = null, Type? callerType = null) =>
        _providers.TryGetValue(providerName ?? _options.DefaultCache, out var cacheProvider) ? cacheProvider.CreateCache() : DefaultCache();

    public IHashCache CreateHashCache(string? providerName = null, Type? entityType = null, Type? callerType = null) =>
        _providers.TryGetValue(providerName ?? _options.DefaultCache, out var cacheProvider) ? cacheProvider.CreateHashCache() : DefaultHashCache();

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

    private ICache DefaultCache() =>
        _providers.TryGetValue(_options.DefaultCache, out var cacheProvider) ? cacheProvider.CreateCache() : NullCache.Instance;

    private IHashCache DefaultHashCache() =>
        _providers.TryGetValue(_options.DefaultCache, out var cacheProvider) ? cacheProvider.CreateHashCache() : NullHashCache.Instance;
}
