using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration.Tests.Support;

public abstract class RouterTestBase : IAsyncDisposable
{
    private readonly List<TestNode> _nodes = [];

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

    internal static Task<RouterMode> NextMode(TestNode node)
    {
        var next = new TaskCompletionSource<RouterMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Router.ModeChanged += (mode, _) => next.TrySetResult(mode);
        return next.Task;
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
        string? scope = null)
    {
        var node = TestNode.Create(Bus, Clock, Store, cacheName ?? CacheName, useLibrary, localCap, pendingWindow, defaultExpiry: defaultExpiry, sharedCore: sharing?.Core, library: library, bypass: bypass, sourceTimeout: sourceTimeout, lazyLibrary: lazyLibrary, lazyLibraryAsync: lazyLibraryAsync, startBound: startBound, cleanupBound: cleanupBound, version: version, cacheNulls: cacheNulls, bridge: bridge, bridgeOrigin: bridgeOrigin, pinWhen: pinWhen, pinStore: pinStore, scope: scope);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Moves the clock in steps until the node has no background work left, waiting for each timer to be armed first.</summary>
    internal async Task RunBackgroundAsync(TestNode node, TimeSpan step)
    {
        for (var rounds = 0; rounds < 200; rounds++)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Clock.HasTimers && DateTime.UtcNow < deadline)
            {
                if (node.Core.IsIdle)
                {
                    return;
                }

                await Task.Delay(5, Ct);
            }

            if (!Clock.HasTimers)
            {
                return;
            }

            Clock.Advance(step);
            await Task.Delay(20, Ct);
        }

        throw new TimeoutException("background work never finished");
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

    /// <summary>Waits for a timer to be armed, then moves the clock.</summary>
    internal async Task AdvanceWhenArmedAsync(TimeSpan by)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!Clock.HasTimers && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        Clock.Advance(by);
        await Task.Delay(20, Ct);
    }

    internal async Task AdvanceUntilAsync(Task task, TimeSpan step)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            await AdvanceWhenArmedAsync(step);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(1), Ct);
    }
}
