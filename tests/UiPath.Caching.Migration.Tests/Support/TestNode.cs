using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>One simulated node: its own router core and local tier over backends shared with the other nodes.</summary>
internal sealed class TestNode : IAsyncDisposable
{
    private readonly bool _ownsCore;
    private readonly ManualTimeProvider _clock;
    private readonly Router _router;
    private bool _stopped;

    private TestNode(string cacheName, Uri source, RouterCore core, RoutedCache<Item> cache, FakeBackend sourceBackend, FakeBackend libraryBackend, bool ownsCore, ManualTimeProvider clock)
    {
        _ownsCore = ownsCore;
        _clock = clock;
        _router = new Router(core, [(cacheName, typeof(Item), cache)]);
        CacheName = cacheName;
        Source = source;
        Core = core;
        Cache = cache;
        SourceBackend = sourceBackend;
        LibraryBackend = libraryBackend;
    }

    public string CacheName { get; }

    public Uri Source { get; }

    public RouterCore Core { get; }

    public RoutedCache<Item> Cache { get; }

    public FakeBackend SourceBackend { get; }

    public FakeBackend LibraryBackend { get; }

    public static TestNode Create(
        FakeBus bus,
        ManualTimeProvider clock,
        Dictionary<string, Item> store,
        string cacheName,
        Func<string, bool>? useLibrary = null,
        TimeSpan? localCap = null,
        TimeSpan? pendingWindow = null,
        string? node = null,
        string group = "g",
        TimeSpan? defaultExpiry = null,
        RouterCore? sharedCore = null,
        IPathClient<Item>? library = null)
    {
        var source = new Uri($"urn:test-{node ?? Guid.NewGuid().ToString("N")}");
        var sourceBackend = new FakeBackend(store, clock);
        var libraryBackend = new FakeBackend(store, clock);
        var core = sharedCore ?? new RouterCore(clock, useLibrary ?? (_ => true), NullLogger.Instance);
        var registration = new RouterCacheRegistration<Item>
        {
            Name = cacheName,
            Group = group,
            Source = sourceBackend,
            RedisKey = key => key,
            LocalCap = localCap ?? TimeSpan.FromMinutes(1),
            PendingWindow = pendingWindow ?? TimeSpan.FromSeconds(30),
            DefaultExpiry = defaultExpiry,
        };
        var events = new CacheEventFactory(Options.Create(new CacheOptions { SourceUri = source }));
        var cache = new RoutedCache<Item>(core, registration, library ?? libraryBackend, bus.For(source), events, source);
        return new TestNode(cacheName, source, core, cache, sourceBackend, libraryBackend, sharedCore is null, clock);
    }

    public bool IsLocal(string key) => Core.Tier.TryGet(new LocalKey(CacheName, key), EpochOf(Cache), out _);

    /// <summary>The router's own stop, with the clock moved on while it waits.</summary>
    public async Task StopAsync(TimeSpan? step = null)
    {
        var stop = StartStop();
        while (!stop.IsCompleted)
        {
            await Task.WhenAny(stop, Task.Delay(20));
            if (!stop.IsCompleted)
            {
                _clock.Advance(step ?? TimeSpan.FromSeconds(1));
            }
        }

        await stop;
    }

    /// <summary>Starts the router's stop and leaves the clock alone.</summary>
    public Task StartStop(CancellationToken cancellationToken = default)
    {
        _stopped = true;
        return _router.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsCore && !_stopped)
        {
            Core.BeginStop();
            Core.CancelBackground();
            await Core.DrainAsync();
        }

        if (!_stopped)
        {
            Cache.Dispose();
            if (_ownsCore)
            {
                Core.DisposeResources();
            }
        }
    }

    private static CacheEpoch EpochOf(RoutedCache<Item> cache) => cache.Epoch;
}
