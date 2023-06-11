namespace UiPath.Platform.Caching.Broadcast;

public sealed class TopicFactory : ITopicFactory, IDisposable
{
    private readonly IDictionary<string, ITopicProvider> _providers = new Dictionary<string, ITopicProvider>(StringComparer.OrdinalIgnoreCase);
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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TopicFactory));
        }

        if (!provider.Enabled)
        {
            return;
        }

        _providers[provider.Name] = provider;
    }

    public void Dispose()
    {
        _providers.Clear();
        _disposed = true;
    }

    public ITopicProvider Get(string? providerName, Type entryType) =>
        _providers.TryGetValue(providerName ?? _options.DefaultTopic, out var provider) ? provider : Default();

    private ITopicProvider Default() =>
        _providers.TryGetValue(_options.DefaultTopic, out var provider) ? provider : NullTopicProvider.Instance;
}
