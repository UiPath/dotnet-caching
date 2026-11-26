using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>One simulated node of a routed hash cache over a source and a library backend that share one store.</summary>
internal sealed class HashTestNode : IAsyncDisposable
{
    private readonly RouterCore _core;
    private bool _stopped;

    private HashTestNode(string name, RouterCore core, RoutedHashCache<Item> cache, FakeHashBackend source, FakeHashBackend library)
    {
        _core = core;
        CacheName = name;
        Cache = cache;
        Source = source;
        Library = library;
        Router = new Router(core, [(name, typeof(IHashCache<Item>), cache)]);
    }

    public string CacheName { get; }

    public Router Router { get; }

    public RoutedHashCache<Item> Cache { get; }

    public IHashCache<Item> Surface => Cache;

    public FakeHashBackend Source { get; }

    public FakeHashBackend Library { get; }

    public static HashTestNode Create(ManualTimeProvider clock, string name, Func<string, bool>? useLibrary = null, Func<bool>? bypass = null, IHashPathClient<Item>? library = null, TimeSpan? defaultExpiry = null, TimeSpan? pendingWindow = null, Dictionary<string, StoredHash>? store = null, TimeProvider? time = null)
    {
        var shared = store ?? [];
        var source = new FakeHashBackend(shared, clock);
        var backend = new FakeHashBackend(shared, clock);
        var core = new RouterCore(time ?? clock, useLibrary ?? (_ => true), NullLogger.Instance)
        {
            BypassFlag = bypass,
            SourceTimeout = TimeSpan.FromSeconds(30),
            StartBound = TimeSpan.FromSeconds(5),
            CleanupBound = TimeSpan.FromSeconds(10),
        };
        var registration = new RouterHashCacheRegistration<Item>
        {
            Name = name,
            Group = "g",
            Source = source,
            RedisKey = key => key,
            DefaultExpiry = defaultExpiry,
            PendingWindow = pendingWindow ?? TimeSpan.FromSeconds(30),
        };
        return new HashTestNode(name, core, new RoutedHashCache<Item>(core, registration, library ?? backend), source, backend);
    }

    /// <summary>The router's stop, with the clock left alone.</summary>
    public Task StopAsync()
    {
        _stopped = true;
        return Router.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            return;
        }

        _core.BeginStop();
        _core.CancelBackground();
        await _core.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);
        Cache.Dispose();
        _core.DisposeResources();
    }
}
