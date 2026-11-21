using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ControlUpkeepTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData("S0", true, "", false)]
    [InlineData("S1", true, "", true)]
    [InlineData("S4", true, "", true)]
    [InlineData("S1", false, "", false)]
    [InlineData("S5", true, "", true)]
    [InlineData("S5", true, "id", false)]
    public void Upkeep_applies_to_committed_s1_to_s4_and_to_s5_until_lswept_names_the_current_id(string state, bool durable, string swept, bool applies)
    {
        var record = new StateRecord(state, "fwd", 5, "id", durable, 1, string.Empty, null, "a", "cs", string.Empty, swept, string.Empty, string.Empty);

        ControlUpkeep.Applies(record).Should().Be(applies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_healthy_group_needs_nothing_and_a_group_in_s0_is_not_checked(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a", "b");
        (await kit.Upkeep.CheckAsync(kit.Group, init, CancellationToken.None)).Should().Be(UpkeepVerdict.NotApplicable);

        var state = await MoveToS1Async(kit, init);

        (await kit.Upkeep.CheckAsync(kit.Group, state, CancellationToken.None)).Should().Be(UpkeepVerdict.Healthy);
    }

    [Theory]
    [InlineData(true, "record")]
    [InlineData(true, "gate")]
    [InlineData(true, "lower")]
    [InlineData(false, "record")]
    [InlineData(false, "gate")]
    [InlineData(false, "lower")]
    public async Task A_missing_record_a_missing_gate_or_a_lower_epoch_is_rebuilt_under_a_new_identity_with_the_same_state(bool useScripts, string damage)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await MoveToS1Async(kit, await kit.InitAsync("a", "b"));
        var control = kit.Names.Control(kit.Group, 0);
        switch (damage)
        {
            case "record":
                await kit.Db.KeyDeleteAsync(control);
                break;
            case "gate":
                await kit.Db.HashDeleteAsync(control, "c:b");
                break;
            default:
                await kit.Db.HashSetAsync(control, "ep", state.Ep - 1);
                break;
        }

        (await kit.Upkeep.CheckAsync(kit.Group, state, CancellationToken.None)).Should().Be(UpkeepVerdict.Rebuild);
        var result = await kit.Upkeep.RebuildAsync(kit.Group, state, null, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        var rebuilt = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        rebuilt.Should().Match<StateRecord>(s => s.Ep == state.Ep + 1 && s.Id != state.Id && s.State == "S1" && s.Direction == "fwd" && s.Durable && s.Caches == "a+b");
        rebuilt.Prev.Should().Be($"S1:fwd:0:{rebuilt.Ep}:{rebuilt.Id}");
        (await kit.Upkeep.CheckAsync(kit.Group, rebuilt, CancellationToken.None)).Should().Be(UpkeepVerdict.Healthy);
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "c:b");
    }

    [Theory]
    [InlineData(true, "above")]
    [InlineData(true, "other")]
    [InlineData(false, "above")]
    [InlineData(false, "other")]
    public async Task A_record_above_the_state_or_at_its_epoch_under_another_id_is_an_identity_conflict_and_never_rebuilt(bool useScripts, string kind)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await MoveToS1Async(kit, await kit.InitAsync("a"));
        var control = kit.Names.Control(kit.Group, 0);
        if (kind == "above")
        {
            await kit.Db.HashSetAsync(control, "ep", state.Ep + 1);
        }
        else
        {
            await kit.Db.HashSetAsync(control, "id", "intruder");
        }

        (await kit.Upkeep.CheckAsync(kit.Group, state, CancellationToken.None)).Should().Be(UpkeepVerdict.IdentityConflict);
        await kit.Upkeep.CheckOnceAsync(kit.Group, CancellationToken.None);

        kit.Reports.Select(r => r.Kind).Where(k => k != "no_replica").Should().Equal("identity_conflict");
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(state.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_managed_set_changes_in_a_rebuild_of_s1_and_is_frozen_after_x3(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await MoveToS1Async(kit, await kit.InitAsync("a"));

        var result = await kit.Upkeep.RebuildAsync(kit.Group, state, StateRecord.EncodeCaches(["b", "a"]), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        var rebuilt = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        rebuilt.Should().Match<StateRecord>(s => s.Caches == "a+b" && s.CacheSet == StateRecord.HashOfCaches("a+b"));
        (await kit.ControlAsync(0)).Where(e => e.Name.ToString().StartsWith("c:", StringComparison.Ordinal)).Select(e => (string)e.Name!).Should().BeEquivalentTo("c:a", "c:b");

        var frozen = rebuilt with { State = "S3", NPop = 1 };
        var act = () => kit.Upkeep.RebuildAsync(kit.Group, frozen, "a", null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_group_with_a_transition_in_flight_is_not_rebuilt(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0));
        var inFlight = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        var result = await kit.Upkeep.RebuildAsync(kit.Group, inFlight, null, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.StateChanged);
    }

    [Fact]
    public async Task The_leader_checks_every_sixty_seconds_and_a_node_without_the_lease_does_nothing()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var state = await MoveToS1Async(kit, await kit.InitAsync("a"));
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Db.KeyDeleteAsync(control);
        var lease = new LeaderLease(kit.Scripts, kit.Names.Lead(kit.Group), kit.Options, kit.Time);
        using var stop = new CancellationTokenSource();
        var loop = kit.Upkeep.RunAsync(kit.Group, lease, stop.Token);

        await TimeSteps.AdvanceArmedAsync(kit.Time, TimeSpan.FromSeconds(60));
        await TimeSteps.ArmedAsync(kit.Time);
        (await kit.Db.KeyExistsAsync(control)).Should().BeFalse("this node does not hold the lease");

        await lease.TryHoldAsync();
        await TimeSteps.AdvanceArmedAsync(kit.Time, TimeSpan.FromSeconds(59));
        (await kit.Db.KeyExistsAsync(control)).Should().BeFalse("sixty seconds have not passed since the last check");
        await lease.TryHoldAsync();
        await TimeSteps.AdvanceArmedAsync(kit.Time, TimeSpan.FromSeconds(1));
        await TimeSteps.UntilAsync(() => kit.Db.KeyExists(control));

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Ep.Should().Be(state.Ep + 1);
        kit.Reports.Select(r => r.Kind).Should().Contain("rebuild");
        await stop.CancelAsync();
        await loop;
        await lease.ReleaseAsync();
    }

    private static async Task<StateRecord> MoveToS1Async(StateKit kit, StateRecord init)
    {
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        result.Outcome.Should().Be(TransitionOutcome.Completed);
        return (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
    }
}
