namespace UiPath.Caching;

/// <summary>
/// <see cref="ISetCacheProvider"/> for the in-process <see cref="ISetCache"/>. Mirrors
/// <c>InMemoryCacheProvider</c> exactly: it produces a <see cref="MultilayerSetCache"/> over
/// <see cref="NullSetCache.Instance"/>, so the multilayer's local tier is the storage — the
/// multilayer detects the no-op inner and serves mutations from its local tier, the way it serves
/// them while disconnected.
/// </summary>
public sealed class InMemorySetCacheProvider : ISetCacheProvider
{
    private readonly Lazy<MultilayerSetCache> _cache;

    public InMemorySetCacheProvider(
        IMemoryCacheFactory memoryCacheFactory,
        ISerializerProxy<RedisValue> serializer,
        IOptions<InMemorySetCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(memoryCacheFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(options);
        Enabled = options.Value.Enabled;
        _cache = new Lazy<MultilayerSetCache>(() =>
            new MultilayerSetCache(Name, NullSetCache.Instance, memoryCacheFactory, serializer, options.Value,
                localMaxExpiration: null, defaultExpiration: options.Value.DefaultExpiration));
    }

    public string Name => KnownCacheProviderNames.InMemory;

    public bool Enabled { get; }

    public ISetCache CreateSetCache() => _cache.Value;

    public void Dispose()
    {
        if (_cache.IsValueCreated)
        {
            _cache.Value.Dispose();
        }
    }
}
