namespace UiPath.Caching;

public sealed class QueueCacheFactory : IQueueCacheFactory
{
    private readonly Dictionary<string, IQueueCacheProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CacheOptions _options;
    private volatile bool _disposed;

    public QueueCacheFactory(IOptions<CacheOptions> cacheOptions)
        : this(cacheOptions, [])
    {
    }

    public QueueCacheFactory(IOptions<CacheOptions> cacheOptions, IEnumerable<IQueueCacheProvider> providers)
    {
        _options = cacheOptions.Value;
        foreach (var provider in providers)
        {
            AddProvider(provider);
        }
    }

    public IEnumerable<string> ProviderNames => _providers.Values.Where(p => p.Enabled).Select(p => p.Name);

    public void AddProvider(IQueueCacheProvider provider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _providers[provider.Name] = provider;
    }

    public ISetCache CreateSetCache(string? providerName = null) =>
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

    private IQueueCacheProvider? GetProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) && provider.Enabled ? provider : null;
}
