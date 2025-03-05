namespace UiPath.Platform.Caching;

public sealed class CacheFactory : ICacheFactory
{
    private readonly Dictionary<string, ICacheProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CacheOptions _options;
    private volatile bool _disposed;

    public CacheFactory(IOptions<CacheOptions> cacheOptions)
        : this(cacheOptions, [])
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

    public IEnumerable<string> ProviderNames => _providers.Values.Where(p => p.Enabled).Select(p => p.Name);

    public void AddProvider(ICacheProvider provider)
    {
        this.ThrowIfDisposed(_disposed);
        _providers[provider.Name] = provider;
    }

    public ICache CreateCache(string? providerName = null) =>
        GetProvider(providerName ?? _options.DefaultCache)?.CreateCache() ?? DefaultCache();

    public IHashCache CreateHashCache(string? providerName = null) =>
        GetProvider(providerName ?? _options.DefaultCache)?.CreateHashCache() ?? DefaultHashCache();

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

    private ICache DefaultCache() => GetProvider(_options.DefaultCache)?.CreateCache() ?? NullCache.Instance;

    private IHashCache DefaultHashCache() => GetProvider(_options.DefaultCache)?.CreateHashCache() ?? NullHashCache.Instance;

    private ICacheProvider? GetProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) && provider.Enabled ? provider : null;
}
