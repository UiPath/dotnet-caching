using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The router's key move operations rely on the local fill mark, the dirty flag, and an NX fill that returns the existing value.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveRouterTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The promotion runs inside the router's read, whose mark was taken before it. A write that lands during the promotion evicts the tier and moves the mark,
    // so the value the read returns cannot be cached after the write's eviction. Without the mark the tier would hold the value it was handed.
    [Fact]
    public async Task A_value_read_while_a_write_raced_the_promotion_is_not_cached_locally()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, new FakeBus());
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                await node.Cache.SetAsync("k", new Item("v1"), CacheExpiry.After(TimeSpan.FromMinutes(5)), Ct);
            }
        };

        var read = await node.Cache.TryGetAsync("k", Ct);

        read!.Name.Should().Be("v1");
        node.IsLocal("k").Should().BeFalse();

        // The control: a read that starts after the write may cache it, so the tier is not simply closed.
        kit.Schedule.Hook = null;
        (await node.Cache.TryGetAsync("k", Ct))!.Name.Should().Be("v1");
        node.IsLocal("k").Should().BeTrue();
    }

    // An NX fill that loses to another writer discards the factory's value and returns the stored one.
    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S5", true)]
    public async Task A_fill_that_loses_returns_the_stored_value_and_changes_nothing_on_the_scripts_and_the_fallback(string state, bool scripts)
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, clock: clock);
        await using var node = NodeOf(kit, clock, new FakeBus());
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, nPop: state == "S1" ? 0 : 1);
        var side = state == "S1" ? kit.L("k") : kit.N("k");

        var got = await node.Cache.GetOrAddAsync(
            "k",
            async _ =>
            {
                await kit.Db.StringSetAsync(side, "other");
                return new Item("generated");
            },
            default,
            Ct);

        got!.Name.Should().Be("other");
        (await kit.GetAsync(side)).Should().Be("other");
        node.IsLocal("k").Should().BeTrue();
    }

    [Fact]
    public async Task A_fill_that_wins_stores_the_value_and_replaces_the_fence_of_a_promoter_that_captured_earlier()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, new FakeBus());
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "captured-before");

        (await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("generated")), default, Ct))!.Name.Should().Be("generated");

        (await kit.GetAsync(kit.N("k"))).Should().Be("generated");
        (await kit.FenceAsync("k")).Should().NotBe("captured-before");
    }

    // The fence refuses a promotion that raced a delete, so the value cannot be resurrected even without a tombstone.
    [Fact]
    public async Task A_promotion_that_raced_a_delete_does_not_resurrect_the_value_without_a_tombstone()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, new FakeBus());
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                await node.Cache.RemoveAsync("k", Ct);
            }
        };

        var read = await node.Cache.TryGetAsync("k", Ct);

        read.Should().BeNull();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task An_own_write_is_visible_at_once_the_tier_is_evicted_and_the_event_goes_out_after_the_commit()
    {
        var clock = NewClock();
        var bus = new FakeBus();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, bus);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        (await node.Cache.TryGetAsync("k", Ct)).Should().BeNull();
        await node.Cache.SetAsync("k", new Item("v0"), default, Ct);
        (await node.Cache.TryGetAsync("k", Ct))!.Name.Should().Be("v0");
        node.IsLocal("k").Should().BeTrue();
        string? atPublish = null;
        var evictedAtPublish = true;
        bus.OnPublish = _ =>
        {
            atPublish = kit.Db.StringGet(kit.N("k"));
            evictedAtPublish = !node.IsLocal("k");
        };

        await node.Cache.SetAsync("k", new Item("v1"), default, Ct);

        atPublish.Should().Be("v1", "published={0} steps={1}", bus.Published, string.Join(',', kit.Schedule.Steps));
        evictedAtPublish.Should().BeTrue();
        (await node.Cache.TryGetAsync("k", Ct))!.Name.Should().Be("v1");
    }

    [Fact]
    public async Task A_promotion_publishes_no_event_and_the_local_copy_is_held_to_the_cap()
    {
        var clock = NewClock();
        var bus = new FakeBus();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, bus, TimeSpan.FromSeconds(2));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        var before = bus.Published;

        (await node.Cache.TryGetAsync("k", Ct))!.Name.Should().Be("v0");

        bus.Published.Should().Be(before);
        node.IsLocal("k").Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(3));
        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task A_gap_clears_the_tier_and_every_hint()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = NodeOf(kit, clock, new FakeBus());
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S3");
        await node.Cache.SetAsync("b", new Item("held"), default, Ct);
        await node.Cache.TryGetAsync("b", Ct);
        await node.Cache.TryGetAsync("a", Ct);
        node.IsLocal("b").Should().BeTrue();
        kit.Hints().Count.Should().Be(1);

        node.Cache.ResetLocal();

        node.IsLocal("b").Should().BeFalse();
        kit.Hints().Count.Should().Be(0);
    }

    [Fact]
    public async Task A_managed_cache_in_key_move_never_falls_back_to_the_source_for_a_write_that_the_state_closes()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var backend = new Dictionary<string, Item>();
        await using var node = TestNode.Create(new FakeBus(), clock, backend, "kmr" + kit.Unique, library: kit.Client);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");

        var write = async () => await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        backend.Should().BeEmpty();
    }

    private static ManualTimeProvider NewClock() => new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    private static TestNode NodeOf(KeyMoveKit kit, ManualTimeProvider clock, FakeBus bus, TimeSpan? localCap = null) =>
        TestNode.Create(bus, clock, [], "kmr" + kit.Unique, library: kit.Client, localCap: localCap ?? TimeSpan.FromMinutes(1));
}
