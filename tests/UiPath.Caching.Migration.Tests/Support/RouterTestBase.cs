using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration.Tests.Support;

public abstract class RouterTestBase : IAsyncDisposable
{
    private readonly List<TestNode> _nodes = [];
    private readonly List<IAsyncDisposable> _owned = [];

    internal ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    internal FakeBus Bus { get; } = new();

    internal Dictionary<string, Item> Store { get; } = [];

    internal string CacheName { get; } = $"cache{Guid.NewGuid():N}"[..14];

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }

    internal static async Task WaitForLibraryAsync(IRouter router, CancellationToken token)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(RouterMode mode, RouterModeReason reason)
        {
            if (mode == RouterMode.Library)
            {
                ready.TrySetResult();
            }
        }

        router.ModeChanged += Changed;
        try
        {
            Changed(router.Mode, router.ModeReason);
            await ready.Task.WaitAsync(token);
        }
        finally
        {
            router.ModeChanged -= Changed;
        }
    }

    internal static Task<RouterMode> NextMode(TestNode node)
    {
        var next = new TaskCompletionSource<RouterMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Router.ModeChanged += (mode, _) => next.TrySetResult(mode);
        return next.Task;
    }

    internal static Task RunBackgroundAsync(TestNode node, TimeSpan step) =>
        node.Time.AdvanceUntilAsync(node.Core.DrainAsync(), step, Ct);

    /// <summary>Hands a node to the test base, which disposes it when the test ends.</summary>
    internal TNode Own<TNode>(TNode node)
        where TNode : IAsyncDisposable
    {
        _owned.Add(node);
        return node;
    }

    internal TestNode NewNode(
        Func<string, bool>? useLibrary = null,
        TimeSpan? localCap = null,
        TimeSpan? pendingWindow = null,
        string? cacheName = null,
        TimeSpan? defaultExpiry = null,
        TestNode? sharing = null,
        IPathClient<Item>? library = null,
        Func<bool>? bypass = null,
        TimeSpan? sourceTimeout = null,
        Func<LibraryParts>? lazyLibrary = null,
        Func<CancellationToken, Task<LibraryParts>>? lazyLibraryAsync = null,
        TimeSpan? startBound = null,
        TimeSpan? cleanupBound = null,
        Func<string>? version = null,
        bool cacheNulls = false,
        IChannelBridge? bridge = null,
        string? bridgeOrigin = null,
        Func<string, bool>? pinWhen = null,
        IPinStore? pinStore = null,
        string? scope = null,
        TimeProvider? startupClock = null)
    {
        var node = TestNode.Create(Bus, Clock, Store, cacheName ?? CacheName, useLibrary, localCap, pendingWindow, defaultExpiry: defaultExpiry, sharedCore: sharing?.Core, library: library, bypass: bypass, sourceTimeout: sourceTimeout, lazyLibrary: lazyLibrary, lazyLibraryAsync: lazyLibraryAsync, startBound: startBound, cleanupBound: cleanupBound, version: version, cacheNulls: cacheNulls, bridge: bridge, bridgeOrigin: bridgeOrigin, pinWhen: pinWhen, pinStore: pinStore, scope: scope, startupClock: startupClock);
        _nodes.Add(node);
        return node;
    }

    internal LibraryParts Parts(FakeMigrationCache store, IReadOnlyList<IHostedService>? started = null)
    {
        var source = new Uri($"urn:lazy-{Guid.NewGuid():N}");
        return new LibraryParts(
            store,
            new FakeConnection(),
            Bus.For(source),
            new CacheEventFactory(Options.Create(new CacheOptions { SourceUri = source })),
            source,
            () => TimeSpan.FromSeconds(30),
            started);
    }
}
