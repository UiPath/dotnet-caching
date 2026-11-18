using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class EventTests : RouterTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_peer_write_evicts_the_key_on_this_node(bool library)
    {
        var node = NewNode();
        var peer = NewNode(useLibrary: _ => library);
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);

        await peer.Cache.SetAsync("k", new Item("new"), default, Ct);

        node.IsLocal("k").Should().BeFalse();
        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("new"));
    }

    [Fact]
    public async Task A_peer_remove_evicts_the_key_on_this_node()
    {
        var node = NewNode();
        var peer = NewNode();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);

        await peer.Cache.RemoveAsync("k", Ct);

        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task Events_are_scoped_to_one_cache_by_its_own_topic()
    {
        var other = $"other{Guid.NewGuid():N}"[..14];
        var node = NewNode();
        var nodeOther = NewNode(cacheName: other);
        var peer = NewNode();
        Store["k"] = new Item("v");
        await node.Cache.TryGetAsync("k", Ct);
        await nodeOther.Cache.TryGetAsync("k", Ct);

        await peer.Cache.RemoveAsync("k", Ct);
        Store["k"] = new Item("v");

        node.IsLocal("k").Should().BeFalse();
        nodeOther.IsLocal("k").Should().BeTrue("the removal was of the other cache's key");
    }

    [Fact]
    public async Task A_peer_clear_drops_only_that_caches_local_entries()
    {
        var other = $"other{Guid.NewGuid():N}"[..14];
        var node = NewNode();
        var nodeOther = NewNode(cacheName: other);
        var peer = NewNode();
        Store["a"] = new Item("v");
        await node.Cache.TryGetAsync("a", Ct);
        await nodeOther.Cache.TryGetAsync("a", Ct);

        await peer.Cache.ClearAsync(Ct);

        node.IsLocal("a").Should().BeFalse();
        nodeOther.IsLocal("a").Should().BeTrue();
    }

    [Theory]
    [InlineData(MissedEventsReason.Lost)]
    [InlineData(MissedEventsReason.SubscriptionGap)]
    public async Task Any_missed_events_signal_clears_the_local_entries_of_the_cache(MissedEventsReason reason)
    {
        var node = NewNode();
        Store["a"] = new Item("v");
        Store["b"] = new Item("v");
        await node.Cache.TryGetAsync("a", Ct);
        await node.Cache.TryGetAsync("b", Ct);

        Bus.Gap(node.Source, reason);

        node.IsLocal("a").Should().BeFalse();
        node.IsLocal("b").Should().BeFalse();
    }

    [Fact]
    public async Task An_event_from_this_node_is_ignored()
    {
        var node = NewNode();
        Store["k"] = new Item("v");
        await node.Cache.TryGetAsync("k", Ct);
        var evicted = new List<string>();
        var observer = new PeerObserver(node.Source, evicted.Add, () => evicted.Add("*"));
        var factory = new CacheEventFactory(Microsoft.Extensions.Options.Options.Create(new CacheOptions { SourceUri = node.Source }));
        var own = factory.Create(CacheName, KnownEventTypes.CacheRemoved, new CacheEventData(CacheKey.Verbatim("k")));
        var otherFactory = new CacheEventFactory(Microsoft.Extensions.Options.Options.Create(new CacheOptions { SourceUri = new Uri("urn:elsewhere") }));
        var foreign = otherFactory.Create(CacheName, KnownEventTypes.CacheRemoved, new CacheEventData(CacheKey.Verbatim("k")));

        observer.OnNext(own);
        evicted.Should().BeEmpty();
        observer.OnNext(foreign);

        evicted.Should().Equal("k");
    }

    [Fact]
    public async Task Event_keys_keep_the_exact_text_of_the_caller_key()
    {
        var node = NewNode();

        await node.Cache.SetAsync(" Mixed:Case ", new Item("v"), default, Ct);

        Bus.Events.Single().Key.Should().Be(" Mixed:Case ");
        Store.Should().ContainKey(" Mixed:Case ");
    }
}
