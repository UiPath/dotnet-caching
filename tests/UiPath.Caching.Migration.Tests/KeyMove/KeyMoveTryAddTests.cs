using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A create returns true only to the caller that created the key, in every state, with both scripts and the fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveTryAddTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S2", true)]
    public async Task S1_and_S2_create_by_SRCSET_nx_and_the_second_creator_loses(string state, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, nPop: 0);

        (await kit.Client.TryAddAsync("k", new Item("claim-a"), TimeSpan.FromMinutes(5), Ct)).Kind.Should().Be(TryAddKind.Created);
        var second = await kit.Client.TryAddAsync("k", new Item("claim-b"), TimeSpan.FromMinutes(5), Ct);

        second.Kind.Should().Be(TryAddKind.Exists);
        second.Existing!.Name.Should().Be("claim-a");
        (await kit.GetAsync(kit.L("k"))).Should().Be("claim-a");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S2_closes_NoOrigin_and_AtMostOnce_creates(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S2", nPop: 0);

        var create = async () => await kit.Client.TryAddAsync("k", new Item("claim"), null, Ct);

        await create.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S3_captures_rechecks_the_union_and_publishes_nx_with_a_relative_life_under_a_replaced_fence(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "before");

        (await kit.Client.TryAddAsync("k", new Item("claim"), TimeSpan.FromMinutes(5), Ct)).Kind.Should().Be(TryAddKind.Created);

        (await kit.GetAsync(kit.N("k"))).Should().Be("claim");
        (await kit.FenceAsync("k")).Should().NotBe("before");
        var ttl = (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value;
        ttl.Should().BeGreaterThan(TimeSpan.FromMinutes(4)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        var steps = kit.Schedule.Steps.ToList();
        steps.IndexOf(SchedulePointStep.Capture).Should().BeLessThan(steps.IndexOf(SchedulePointStep.Publish));
        steps.Count(s => s == SchedulePointStep.ProbeSource).Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task S3_returns_false_for_a_claim_that_lives_in_L_and_promotes_it()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "claim-l");

        var result = await kit.Client.TryAddAsync("k", new Item("mine"), null, Ct);

        result.Kind.Should().Be(TryAddKind.Exists);
        result.Existing!.Name.Should().Be("claim-l");
        (await kit.GetAsync(kit.N("k"))).Should().Be("claim-l");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_creators_racing_through_the_capture_have_one_winner_because_a_create_replaces_the_fence(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        TryAddResult<Item> inner = default;
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                // The second creator runs whole between the first one's capture and publish.
                inner = await kit.Client.TryAddAsync("k", new Item("claim-b"), null, Ct);
            }
        };

        var outer = await kit.Client.TryAddAsync("k", new Item("claim-a"), null, Ct);

        inner.Kind.Should().Be(TryAddKind.Created);
        outer.Kind.Should().Be(TryAddKind.Exists);
        (await kit.GetAsync(kit.N("k"))).Should().Be("claim-b");
    }

    [Fact]
    public async Task S4_creates_by_FILL_after_the_union_check_and_S5_by_a_native_NX()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S4");
        await kit.Db.StringSetAsync(kit.L("a"), "residue");

        (await kit.Client.TryAddAsync("a", new Item("mine"), null, Ct)).Kind.Should().Be(TryAddKind.Exists);
        (await kit.Client.TryAddAsync("b", new Item("mine"), null, Ct)).Kind.Should().Be(TryAddKind.Created);
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.Fill);
        (await kit.GetAsync(kit.N("b"))).Should().Be("mine");

        await kit.SetStateAsync("S5");
        await kit.Db.StringSetAsync(kit.L("c"), "ignored-residue");
        (await kit.Client.TryAddAsync("c", new Item("mine"), null, Ct)).Kind.Should().Be(TryAddKind.Created);
        (await kit.Client.TryAddAsync("c", new Item("again"), null, Ct)).Kind.Should().Be(TryAddKind.Exists);
        (await kit.GetAsync(kit.N("c"))).Should().Be("mine");
    }

    [Fact]
    public async Task S0_creates_through_the_source_side_and_a_dead_life_is_refused_before_any_send()
    {
        RecordingMoveSide? side = null;
        await using var kit = await KeyMoveKit.CreateAsync(
            fixture,
            source: k => side = new RecordingMoveSide(new InPlaceMoveSide<Item>(k.SidePlane, k.Group, k.L, ItemCodec.Instance, null, k.Clock)));
        await kit.SetStateAsync("S0");

        (await kit.Client.TryAddAsync("k", new Item("a"), null, Ct)).Kind.Should().Be(TryAddKind.Created);
        (await kit.Client.TryAddAsync("k", new Item("b"), null, Ct)).Kind.Should().Be(TryAddKind.Exists);
        var sends = side!.Calls.Count;
        (await kit.Client.TryAddAsync("z", new Item("b"), TimeSpan.Zero, Ct)).Kind.Should().Be(TryAddKind.Failed);

        side.Calls.Count(call => call == "Create").Should().Be(2);
        side.Calls.Count.Should().Be(sends);
        kit.Plain.TryAdds.Should().Be(0);
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reverse_state_closes_every_create()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", direction: "rev");

        var create = async () => await kit.Client.TryAddAsync("k", new Item("a"), null, Ct);

        await create.Should().ThrowAsync<CacheUnavailableException>();
    }
}
