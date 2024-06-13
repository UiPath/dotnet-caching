namespace UiPath.Platform.Caching.Broadcast;

public sealed class TopicFactory : ITopicFactory, IDisposable
{
    private readonly Dictionary<string, ITopicProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CacheOptions _options;
    private volatile bool _disposed;

    public TopicFactory(IOptions<CacheOptions> cacheOptions)
        : this(cacheOptions, Array.Empty<ITopicProvider>())
    {
    }

    public TopicFactory(IOptions<CacheOptions> cacheOptions, IEnumerable<ITopicProvider> providers)
    {
        _options = cacheOptions.Value;
        foreach (var provider in providers)
        {
            AddProvider(provider);
        }
    }

    public void AddProvider(ITopicProvider provider)
    {
        this.ThrowIfDisposed(_disposed);
        _providers[provider.Name] = provider;
    }

    public void Dispose()
    {
        _providers.Clear();
        _disposed = true;
    }

    public ITopicProvider Get(string? providerName = null) =>
        GetProvider(providerName ?? _options.DefaultTopic) ?? Default();

    private ITopicProvider Default() =>
        GetProvider(_options.DefaultTopic) ?? NullTopicProvider.Instance;

    private ITopicProvider? GetProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) && provider.Enabled ? provider : null;
}
