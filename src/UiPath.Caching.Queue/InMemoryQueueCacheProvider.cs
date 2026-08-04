using UiPath.Caching.Locking;

namespace UiPath.Caching;

/// <summary>
/// <see cref="IQueueCacheProvider"/> for the in-process backing. Mirrors
/// <c>InMemoryCacheProvider</c> exactly: it produces a <see cref="MultilayerSetCache"/> over
/// <see cref="NullSetCache.Instance"/>, so the multilayer's local tier is the storage — the
/// multilayer detects the no-op inner and serves mutations from its local tier, the way it serves
/// them while disconnected.
/// </summary>
public sealed class InMemoryQueueCacheProvider : IQueueCacheProvider
{
    private readonly InMemoryQueueCacheOptions _options;
    private readonly IMemoryCacheFactory _memoryCacheFactory;
    private readonly ISerializerProxy<RedisValue> _serializer;
    private readonly ILocalLock _localLock;
    private readonly Lazy<MultilayerSetCache> _setCache;

    public string Name => KnownCacheProviderNames.InMemory;

    public bool Enabled { get; }

    public InMemoryQueueCacheProvider(
        IOptions<InMemoryQueueCacheOptions> optionsAccessor,
        IMemoryCacheFactory memoryCacheFactory,
        ISerializerProxy<RedisValue> serializer,
        ILocalLock localLock)
    {
        _options = optionsAccessor.Value;
        _memoryCacheFactory = memoryCacheFactory;
        _serializer = serializer;
        _localLock = localLock;
        _setCache = new Lazy<MultilayerSetCache>(() => BuildSetCache());
        Enabled = _options.Enabled;
    }

    public ISetCache CreateSetCache() =>
        _setCache.Value;

    public void Dispose()
    {
        if (_setCache.IsValueCreated)
        {
            _setCache.Value.Dispose();
        }
    }

    private MultilayerSetCache BuildSetCache() =>
        new(
            Name,
            NullSetCache.Instance,
            _memoryCacheFactory,
            _serializer,
            _options,
            _localLock,
            localMaxExpiration: null,
            defaultExpiration: _options.DefaultExpiration);
}
