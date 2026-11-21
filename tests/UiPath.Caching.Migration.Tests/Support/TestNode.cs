using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>One simulated node: its own router core and local tier over backends shared with the other nodes.</summary>
internal sealed class TestNode : IAsyncDisposable
{
    private readonly bool _ownsCore;
    private readonly ManualTimeProvider _clock;
    private bool _stopped;
    private bool _disposed;

    private TestNode(string cacheName, Uri source, RouterCore core, RoutedCache<Item> cache, FakeBackend sourceBackend, FakeBackend libraryBackend, bool ownsCore, ManualTimeProvider clock)
    {
        _ownsCore = ownsCore;
        _clock = clock;
        Router = new Router(core, [(cacheName, typeof(Item), cache)]);
        CacheName = cacheName;
        Source = source;
        Core = core;
        Cache = cache;
        SourceBackend = sourceBackend;
        LibraryBackend = libraryBackend;
    }

    public string CacheName { get; }

    public Router Router { get; }

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
        IPathClient<Item>? library = null,
        Func<bool>? bypass = null,
        TimeSpan? sourceTimeout = null,
        Func<LibraryParts>? lazyLibrary = null,
        Func<CancellationToken, Task<LibraryParts>>? lazyLibraryAsync = null,
        TimeSpan? startBound = null,
        TimeSpan? cleanupBound = null,
        Func<string>? version = null,
        bool cacheNulls = false)
    {
        var source = new Uri($"urn:test-{node ?? Guid.NewGuid().ToString("N")}");
        var sourceBackend = new FakeBackend(store, clock);
        var libraryBackend = new FakeBackend(store, clock);
        var core = sharedCore ?? new RouterCore(clock, useLibrary ?? (_ => true), NullLogger.Instance)
        {
            BypassFlag = bypass,
            SourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(30),
            StartBound = startBound ?? TimeSpan.FromSeconds(5),
            CleanupBound = cleanupBound ?? TimeSpan.FromSeconds(10),
        };
        var registration = new RouterCacheRegistration<Item>
        {
            Name = cacheName,
            Group = group,
            Source = sourceBackend,
            RedisKey = key => key,
            LocalCap = localCap ?? TimeSpan.FromMinutes(1),
            PendingWindow = pendingWindow ?? TimeSpan.FromSeconds(30),
            DefaultExpiry = defaultExpiry,
            Version = version,
            CacheNulls = cacheNulls,
        };
        var events = new CacheEventFactory(Options.Create(new CacheOptions { SourceUri = source }));
        RoutedCache<Item> cache;
        if (lazyLibrary is null && lazyLibraryAsync is null)
        {
            cache = new RoutedCache<Item>(core, registration, library ?? libraryBackend, bus.For(source), events, source, version?.Invoke());
            return new TestNode(cacheName, source, core, cache, sourceBackend, libraryBackend, sharedCore is null, clock);
        }

        core.UseLibrary(lazyLibraryAsync ?? (_ => Task.FromResult(lazyLibrary!())));
        cache = new RoutedCache<Item>(core, registration, version?.Invoke());
        var lazy = new TestNode(cacheName, source, core, cache, sourceBackend, libraryBackend, sharedCore is null, clock);
        lazy.Router.Start();
        return lazy;
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

    /// <summary>The router's dispose, as a provider runs it, with the clock left alone.</summary>
    public Task StartDispose()
    {
        _stopped = true;
        return Router.DisposeAsync().AsTask();
    }

    /// <summary>Starts the router's stop and leaves the clock alone.</summary>
    public Task StartStop(CancellationToken cancellationToken = default)
    {
        _stopped = true;
        return Router.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsCore && !_stopped)
        {
            Core.BeginStop();
            Core.CancelBackground();
            // Real time: the injected clock stands still at teardown, so nothing that waits on it could end.
            await Core.DrainAsync().WaitAsync(TimeSpan.FromSeconds(20));
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
