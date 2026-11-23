using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// ALLACK writes the time of S3 in the same step as the acknowledgement, so one is never found without the other.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class AllAckT3Tests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_acknowledgement_stores_both_and_a_repeat_keeps_the_first_time(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");

        (await kit.Store.AllAckAsync(kit.Group, state.Id, null, 1000)).Should().BeTrue();
        (await kit.Store.AllAckAsync(kit.Group, state.Id, null, 2000)).Should().BeTrue();

        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        stored.AllAck.Should().Be(state.Id);
        stored.Field(StateFields.T3At).Should().Be("1000");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_acknowledgement_of_another_identity_or_without_the_lease_writes_neither(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");
        var lead = kit.Names.Lead(kit.Group);
        await kit.Db.StringSetAsync(lead, "successor");

        (await kit.Store.AllAckAsync(kit.Group, "old", null, 1000)).Should().BeFalse();
        (await kit.Store.AllAckAsync(kit.Group, state.Id, new LeaseFence(lead, "stale"), 1000)).Should().BeFalse();

        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        stored.AllAck.Should().BeEmpty();
        stored.Field(StateFields.T3At).Should().BeEmpty();
        await kit.Db.KeyDeleteAsync(lead);
    }

    [Fact]
    public async Task An_acknowledgement_of_another_state_than_S3_stores_no_time()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var state = await kit.InitAsync("a");

        await kit.Store.AllAckAsync(kit.Group, state.Id);

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Field(StateFields.T3At).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_never_started_guard_holds_for_an_init_ed_group_and_fails_once_a_move_stamped_it(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var key = kit.StateKey;

        (await kit.Scripts.GuardAsync(key, init.Id, null, neverStarted: true)).Should().Be(1);
        var started = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0) with { MoveId = init.Ep + 1 }, null, CancellationToken.None);
        started.Outcome.Should().Be(TransitionOutcome.Completed);
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        (await kit.Scripts.GuardAsync(key, s1.Id, null, neverStarted: true)).Should().Be(MoveScripts.GroupStarted);
        var back = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S0", "none", 0), null, CancellationToken.None);
        back.Outcome.Should().Be(TransitionOutcome.Completed);
        var s0 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        (await kit.Scripts.GuardAsync(key, s0.Id, null, neverStarted: true)).Should().Be(MoveScripts.GroupStarted, "S0 again, but moveId stays");
        (await kit.Scripts.GuardAsync(key, s0.Id, null)).Should().Be(1);
    }

    [Fact]
    public async Task A_reassert_does_not_carry_the_time_of_an_older_S3()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var init = await kit.InitAsync("a");
        await kit.Store.AllAckAsync(kit.Group, init.Id, null, 1000);
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, new ControlRecord(init.Ep + 10, "rid", "S3", "fwd", "1", "S2", "fwd", "0", "1", "before", [("a", "n")]));
        await kit.Scripts.ControlCommitAsync(control, init.Ep + 10, "rid");

        (await kit.Recovery.ReassertAsync(kit.Group, "operator confirmed", null, null, null, CancellationToken.None)).Transition!.Outcome.Should().Be(TransitionOutcome.Completed);

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Field(StateFields.T3At).Should().BeEmpty();
    }
}
