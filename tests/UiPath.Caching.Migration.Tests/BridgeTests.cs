using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class BridgeTests : RouterTestBase
{
    private const string Me = "urn:me-p1?stack=UiPath.Caching.Migration";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_write_delete_and_create_publishes_a_key_message_carrying_this_node(bool library)
    {
        var (node, bridge) = Bridged(useLibrary: _ => library);

        await node.Cache.SetAsync("a", new Item("1"), default, Ct);
        await node.Cache.RemoveAsync("a", Ct);
        await node.Cache.TryAddAsync("b", new Item("2"), default, Ct);

        bridge.Published.Should().Equal(
            new BridgeMessage(BridgeMessageKind.Key, CacheName, "a", Origin: Me),
            new BridgeMessage(BridgeMessageKind.Key, CacheName, "a", Origin: Me),
            new BridgeMessage(BridgeMessageKind.Key, CacheName, "b", Origin: Me));
    }

    [Fact]
    public async Task A_create_that_found_the_key_held_publishes_nothing()
    {
        var (node, bridge) = Bridged();
        Store["k"] = new Item("old");

        (await node.Cache.TryAddAsync("k", new Item("new"), default, Ct)).Should().Be(AddOutcome.Exists);

        bridge.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_clear_publishes_a_clear_message_before_and_after_the_scan()
    {
        var (node, bridge) = Bridged();

        await node.Cache.ClearAsync(Ct);

        bridge.Published.Should().Equal(
            new BridgeMessage(BridgeMessageKind.Clear, CacheName, Origin: Me),
            new BridgeMessage(BridgeMessageKind.Clear, CacheName, Origin: Me));
    }

    [Fact]
    public async Task In_bypass_with_no_library_a_write_still_reaches_the_bridge()
    {
        var never = new TaskCompletionSource<LibraryParts>();
        var (node, bridge) = Bridged(lazyLibraryAsync: token => never.Task.WaitAsync(token), startBound: TimeSpan.FromMilliseconds(1));
        Clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => node.Router.Mode == RouterMode.BypassLibrary);

        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        bridge.Published.Should().ContainSingle(m => m.Kind == BridgeMessageKind.Key && m.Key == "k");
    }

    [Fact]
    public async Task A_received_key_message_evicts_the_local_copy_and_never_publishes()
    {
        var (node, bridge) = Bridged();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue();

        bridge.Deliver(new BridgeMessage(BridgeMessageKind.Key, CacheName, "k", Origin: "urn:other"));

        node.IsLocal("k").Should().BeFalse();
        bridge.Published.Should().BeEmpty("a received message only evicts, so no echo can start");
        Bus.Published.Should().Be(0);
    }

    [Fact]
    public async Task A_message_from_this_node_is_ignored()
    {
        var (node, bridge) = Bridged();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);

        bridge.Deliver(new BridgeMessage(BridgeMessageKind.Key, CacheName, "k", Origin: Me));

        node.IsLocal("k").Should().BeTrue();
    }

    [Theory]
    [InlineData(BridgeMessageKind.Clear)]
    [InlineData(BridgeMessageKind.Tag)]
    public async Task A_received_clear_or_tag_drops_every_local_copy_of_the_cache(BridgeMessageKind kind)
    {
        var (node, bridge) = Bridged();
        Store["a"] = new Item("1");
        Store["b"] = new Item("2");
        await node.Cache.TryGetAsync("a", Ct);
        await node.Cache.TryGetAsync("b", Ct);

        bridge.Deliver(new BridgeMessage(kind, kind == BridgeMessageKind.Clear ? CacheName : null, Tag: kind == BridgeMessageKind.Tag ? "t" : null, Origin: "urn:other"));

        node.IsLocal("a").Should().BeFalse();
        node.IsLocal("b").Should().BeFalse();
    }

    [Theory]
    [InlineData("unknown-cache", "k")]
    [InlineData(null, "k")]
    [InlineData("self", null)]
    public async Task A_message_for_no_registered_cache_or_without_a_key_changes_nothing(string? cache, string? key)
    {
        var (node, bridge) = Bridged();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);

        bridge.Deliver(new BridgeMessage(BridgeMessageKind.Key, cache == "self" ? CacheName : cache, key, Origin: "urn:other"));

        node.IsLocal("k").Should().BeTrue();
    }

    [Fact]
    public async Task A_bridge_that_throws_never_fails_the_write()
    {
        var (node, bridge) = Bridged();
        bridge.PublishFails = new InvalidOperationException("channel down");

        var outcome = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        outcome.Should().Be(WriteOutcome.Committed);
        Store["k"].Name.Should().Be("v");
    }

    [Fact]
    public async Task A_bridge_that_hangs_holds_a_write_no_longer_than_the_source_timeout()
    {
        var (node, bridge) = Bridged(sourceTimeout: TimeSpan.FromSeconds(3));
        bridge.PublishGate = new TaskCompletionSource();

        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await UntilAsync(() => bridge.Published.Count == 1 && Clock.HasTimers);
        write.IsCompleted.Should().BeFalse();
        Clock.Advance(TimeSpan.FromSeconds(3));

        (await write.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task The_router_from_DI_subscribes_the_bridge_and_publishes_under_its_stack_identity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "brtest",
            ["Caching:SourceUri"] = "urn:bridge-host",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        }).Build();
        var bridge = new FakeChannelBridge();
        var source = new FakeBackend(Store, Clock);
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(configuration, router => router
            .Add(new RouterCacheRegistration<Item> { Name = "items", Group = "g", Source = source, RedisKey = key => key })
            .UseLibraryWhen(_ => false)
            .UseEnvelope(new LegacyEnvelopeProxy())
            .UseLibrarySettleBound(TimeSpan.FromSeconds(5))
            .UseNodeIdentity("bridge1")
            .UseChannelBridge(bridge));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IRouter>().Get<Item>("items").SetAsync("k", new Item("v"), default, Ct);

        bridge.Subscribed.Should().BeTrue();
        bridge.Published.Should().ContainSingle().Which.Origin.Should().Be("urn:bridge-host-pbridge1?stack=uipath.caching.migration");
    }

    [Fact]
    public async Task A_subscribe_that_throws_neither_fails_the_router_nor_stays_down()
    {
        var (node, bridge) = Bridged(bridge: new FakeChannelBridge { SubscribeFailures = 2 });
        (await node.Cache.SetAsync("k", new Item("v"), default, Ct)).Should().Be(WriteOutcome.Committed);

        await UntilAsync(() => bridge.SubscribeCalls == 1 && Clock.HasTimers);
        bridge.Subscribed.Should().BeFalse();
        Clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => bridge.SubscribeCalls == 2 && Clock.HasTimers);
        Clock.Advance(TimeSpan.FromSeconds(2));

        await UntilAsync(() => bridge.Subscribed);
    }

    [Fact]
    public async Task An_adapter_that_blocks_before_it_returns_holds_a_write_no_longer_than_the_source_timeout()
    {
        using var block = new ManualResetEventSlim();
        var (node, bridge) = Bridged(sourceTimeout: TimeSpan.FromSeconds(3), bridge: new FakeChannelBridge { BlockBeforeReturn = block });

        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await UntilAsync(() => bridge.Published.Count == 1 && Clock.HasTimers);
        write.IsCompleted.Should().BeFalse();
        Clock.Advance(TimeSpan.FromSeconds(3));

        (await write.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(WriteOutcome.Committed);
        block.Set();
    }

    [Fact]
    public async Task A_publish_past_the_timeout_is_cancelled_in_the_adapter()
    {
        var (node, bridge) = Bridged(sourceTimeout: TimeSpan.FromSeconds(3));
        bridge.PublishGate = new TaskCompletionSource();

        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await UntilAsync(() => bridge.Published.Count == 1 && Clock.HasTimers);
        Clock.Advance(TimeSpan.FromSeconds(3));
        await write.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await UntilAsync(() => bridge.Cancelled == 1);
    }

    [Fact]
    public async Task Publishes_past_the_pending_limit_are_dropped_not_queued()
    {
        var bridge = new FakeChannelBridge { PublishGate = new TaskCompletionSource() };
        var core = new RouterCore(Clock, _ => true, NullLogger.Instance) { SourceTimeout = TimeSpan.FromHours(1) };
        var host = new BridgeHost(bridge, Me, core, NullLogger.Instance) { MaxPending = 2 };

        var publishes = Enumerable.Range(0, 5).Select(i => host.PublishAsync(BridgeMessageKind.Key, "c", $"k{i}").AsTask()).ToArray();
        await UntilAsync(() => bridge.Published.Count == 2);
        await Task.WhenAll(publishes.Skip(2)).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        bridge.Published.Should().HaveCount(2, "three were dropped at once instead of waiting");
        bridge.PublishGate.SetResult();
        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.PublishAsync(BridgeMessageKind.Key, "c", "after");
        bridge.Published.Should().HaveCount(3, "slots free up once the adapter's calls end");
    }

    [Fact]
    public async Task A_subscription_that_throws_on_dispose_does_not_stop_the_cleanup()
    {
        var hosted = new FakeHostedService();
        var (node, bridge) = Bridged(lazyLibraryAsync: _ => Task.FromResult(Parts(new FakeMigrationCache(), [hosted])));
        await UntilAsync(() => node.Router.Mode == RouterMode.Library);
        bridge.DisposeFails = true;

        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        bridge.Subscribed.Should().BeFalse();
        hosted.Stopped.Should().Be(1, "the library is released after the failed detach");
    }

    [Fact]
    public async Task A_stop_while_Subscribe_is_blocked_disposes_the_subscription_when_it_arrives()
    {
        using var gate = new ManualResetEventSlim();
        var (node, bridge) = Bridged(bridge: new FakeChannelBridge { SubscribeGate = gate });
        await UntilAsync(() => bridge.SubscribeEntered);

        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gate.Set();

        await UntilAsync(() => bridge.Disposals == 1);
        bridge.Subscribed.Should().BeFalse("a subscription that arrived after the stop is disposed at once");
    }

    [Theory]
    [InlineData(BridgeMessageKind.Key)]
    [InlineData(BridgeMessageKind.Clear)]
    public async Task A_message_received_during_a_load_keeps_the_loaded_value_off_the_local_tier(BridgeMessageKind kind)
    {
        var (node, bridge) = Bridged();
        Store["k"] = new Item("old");
        var read = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        node.LibraryBackend.AfterRead = async () =>
        {
            read.TrySetResult();
            await release.Task;
        };

        var load = node.Cache.TryGetAsync("k", Ct).AsTask();
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        bridge.Deliver(new BridgeMessage(kind, CacheName, kind == BridgeMessageKind.Key ? "k" : null, Origin: "urn:other"));
        node.LibraryBackend.AfterRead = null;
        release.SetResult();

        (await load.WaitAsync(TimeSpan.FromSeconds(10), Ct))!.Name.Should().Be("old", "the caller still gets what it read");
        node.IsLocal("k").Should().BeFalse("a value read before the eviction is not kept");
    }

    private (TestNode Node, FakeChannelBridge Bridge) Bridged(
        Func<string, bool>? useLibrary = null,
        Func<CancellationToken, Task<LibraryParts>>? lazyLibraryAsync = null,
        TimeSpan? startBound = null,
        TimeSpan? sourceTimeout = null,
        FakeChannelBridge? bridge = null)
    {
        bridge ??= new FakeChannelBridge();
        var node = NewNode(useLibrary: useLibrary, lazyLibraryAsync: lazyLibraryAsync, startBound: startBound, sourceTimeout: sourceTimeout, bridge: bridge, bridgeOrigin: Me);
        if (bridge.SubscribeFailures == 0 && bridge.SubscribeGate is null)
        {
            // The first Subscribe runs off the start path.
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!bridge.Subscribed && DateTime.UtcNow < until)
            {
                Thread.Sleep(5);
            }

            bridge.Subscribed.Should().BeTrue();
        }

        return (node, bridge);
    }
}
