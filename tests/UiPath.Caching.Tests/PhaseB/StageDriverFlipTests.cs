using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// A flip of the flag while a transition is in flight: abandoned before step 4, completed after it (T-B51, T-B21), and the operator's SkipBarrier.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverFlipTests(RedisContainerFixture fixture)
{
    [Fact]
    public async Task A_flip_back_before_step_4_abandons_the_transition_to_the_last_committed_state_at_once()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        var inFlight = await TransitOnlyAsync(kit, ready, "S3", "fwd", 1);
        kit.Fleet.Nodes[0].Fl = 2;

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Abandon);
        outcome.Result!.Outcome.Should().Be(TransitionOutcome.Completed);
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S1" && s.Direction == "fwd" && s.Durable && s.Ep > inFlight.Ep && s.NPop == 0);
    }

    [Fact]
    public async Task A_flip_after_step_4_completes_the_transition_first_and_reads_the_flag_again_from_the_new_state()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        var target = StateKit.TargetFrom(ready, "S3", "fwd", 1);
        (await kit.Kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(ready), target, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);

        // The commit marker of the state is lost in a failover while the records show dur=1: step 4 began.
        await kit.Kit.Db.HashSetAsync(kit.Kit.StateKey, [new HashEntry("dur", "0"), new HashEntry("prev", StateRecord.FormatPrev(ready.State, ready.Direction, ready.NPop, ready.Ep, ready.Id))]);
        kit.Fleet.All(n => n.Fl = 2);

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Abandon);
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S3" && s.Id == target.Id && s.Durable, "the abandon is refused past step 4 and the transition is completed");
    }

    [Fact]
    public async Task X7_in_flight_is_abandoned_when_the_flag_is_raised_before_step_4()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        var inFlight = await TransitOnlyAsync(kit, drained, "S1", "rev", 1);
        kit.Fleet.All(n => n.Fl = 4);

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Abandon);
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S4" && s.Direction == "fwd" && s.Durable && s.Ep > inFlight.Ep);
    }

    [Fact]
    public async Task A_transition_still_asked_is_resumed_only_after_DurRetry()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        var inFlight = await TransitOnlyAsync(kit, ready, "S3", "fwd", 1);

        (await kit.StepAsync(TimeSpan.FromSeconds(9)))!.Plan.Kind.Should().Be(StepKind.None);
        (await kit.StepAsync(TimeSpan.FromSeconds(1)))!.Plan.Kind.Should().Be(StepKind.None, "nine seconds have passed since the leader first saw it");
        (await kit.StateAsync()).Durable.Should().BeFalse();

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Resume);
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.Id == inFlight.Id && s.Durable);
    }

    [Fact]
    public async Task X5_is_abandoned_when_a_view_falls_after_the_gates_and_before_step_4()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync(TimeSpan.FromSeconds(30));
        }

        (await kit.StateAsync()).State.Should().Be("S4", "the views have shown 5 for 90 seconds, short of CloseConfirm");
        kit.Fleet.AfterReads(1, () => kit.Fleet.Nodes[1].Fl = 4);

        var outcome = await kit.StepAsync(TimeSpan.FromSeconds(30));

        outcome!.Plan.Spec!.Code.Should().Be("X5");
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S4" && s.Durable && s.Ep > drained.Ep);
        after.Field(StateFields.ClosedAt).Should().BeEmpty();
    }

    [Fact]
    public async Task SkipBarrier_lets_a_stuck_forward_transition_go_on_with_wait_skipped()
    {
        var options = new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50), EvictionPolicyAttested = true };
        await using var kit = await DriverKit.CreateAsync(fixture, state: options);
        kit.Kit.Topology.Probe = _ => new MasterProbe(1, "noeviction");
        kit.Kit.Topology.Wait = (_, _) => 0;
        await kit.PrepareAsync();

        var first = await kit.StepAsync(TimeSpan.FromSeconds(10));
        first!.Result!.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.StateAsync()).Durable.Should().BeFalse();
        var retried = await kit.StepAsync(TimeSpan.FromSeconds(10));
        retried!.Result!.Outcome.Should().Be(TransitionOutcome.BarrierStuck, "the leader retries and the barrier still fails");

        (await kit.Commands.SkipBarrierAsync(kit.Group, "the replica is gone for good", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        var skipped = await kit.StepAsync();

        skipped!.Result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S1" && s.Durable);
    }

    [Fact]
    public async Task The_flag_back_abandons_a_transition_that_waits_at_a_failed_barrier_with_the_barriers_skipped()
    {
        var options = new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50), EvictionPolicyAttested = true };
        await using var kit = await DriverKit.CreateAsync(fixture, state: options);
        kit.Kit.Topology.Probe = _ => new MasterProbe(1, "noeviction");
        kit.Kit.Topology.Wait = (_, _) => 0;
        await kit.PrepareAsync();
        (await kit.StepAsync(TimeSpan.FromSeconds(10)))!.Result!.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Fleet.All(n => n.Fl = 2);

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Abandon);
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S0" && s.Durable);
    }

    [Fact]
    public async Task A_driver_whose_lease_was_taken_over_is_refused_by_the_server_even_while_it_still_believes_it_leads()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var key = kit.Kit.Names.Lead(kit.Group);
        var old = new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time);
        (await old.TryHoldAsync()).Should().BeTrue();
        kit.Fence = old.Fence;
        await kit.StepUntilAsync("S1");

        // Another node takes the lease over while the first one is paused.
        await kit.Kit.Db.KeyDeleteAsync(key);
        var next = new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time);
        (await next.TryHoldAsync()).Should().BeTrue();
        var before = await kit.StateAsync();
        kit.HoldsLease.Should().BeTrue();

        var outcome = await kit.StepAsync();

        outcome!.Result!.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.Id == before.Id && s.Durable);

        kit.Fence = next.Fence;
        await kit.StepUntilAsync("S3");
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_leader_stores_allAck_and_clears_the_automatic_hold_under_its_fence_and_a_stale_fence_changes_neither()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var key = kit.Kit.Names.Lead(kit.Group);
        var lease = new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time);
        await lease.TryHoldAsync();
        kit.Fence = lease.Fence;
        var ready = await kit.StepUntilAsync("S1");
        await kit.Commands.HoldAsync(kit.Group, "pause");
        await kit.Kit.Store.SetHoldAsync(kit.Group, HoldKind.Automatic, "no_pb:n9");

        kit.Fence = new LeaseFence(key, "stale");
        await kit.StepAsync();
        var refused = await kit.StateAsync();
        refused.AllAck.Should().NotBe(ready.Id);
        refused.HoldAuto.Should().Be("no_pb:n9");

        kit.Fence = lease.Fence;
        await kit.StepAsync();
        var done = await kit.StateAsync();
        done.AllAck.Should().Be(ready.Id);
        done.HoldAuto.Should().BeEmpty();
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    private static async Task<StateRecord> ToDrainedAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        return await kit.StepUntilAsync("S4");
    }

    // Writes only step 1 of a transition: the state at dur=0 and no control record.
    private static async Task<StateRecord> TransitOnlyAsync(DriverKit kit, StateRecord from, string state, string direction, int nPop)
    {
        var target = StateKit.TargetFrom(from, state, direction, nPop);
        (await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateKit.Expecting(from), target)).Should().Be(MoveScripts.Written);
        return await kit.StateAsync();
    }
}
