using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// The review findings that need a real state store: guarded purges, the X5 recheck on resume, and the reverse residue.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverReviewTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_guard_script_checks_the_identity_and_the_lease_and_writes_nothing(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");
        var key = kit.Names.Lead(kit.Group);
        var lease = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        await lease.TryHoldAsync();

        (await kit.Scripts.GuardAsync(kit.StateKey, state.Id, lease.Fence)).Should().Be(MoveScripts.Written);
        (await kit.Scripts.GuardAsync(kit.StateKey, state.Id, null)).Should().Be(MoveScripts.Written);
        (await kit.Scripts.GuardAsync(kit.StateKey, "other", lease.Fence)).Should().Be(0);
        (await kit.Scripts.GuardAsync(kit.StateKey, state.Id, new LeaseFence(key, "stale"))).Should().Be(MoveScripts.LeaseNotHeld);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(state.Id);
        await kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_X3_census_purge_is_refused_under_a_stale_fence_and_runs_under_the_right_one_with_the_guard()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var (lease, key) = await LeaseAsync(kit);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        kit.Data.Native = 4;
        kit.Fence = new LeaseFence(key, "stale");

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        kit.Data.Calls.Should().NotContain("purge_n", "a leader that lost its lease deletes nothing");
        kit.Alerts.Should().Contain(a => a.Code == "lease_lost");
        (await kit.StateAsync()).State.Should().Be("S1");

        kit.Fence = lease.Fence;
        await kit.StepUntilAsync("S3");
        kit.Data.Guards.Should().NotBeEmpty().And.OnlyContain(g => g.Id == ready.Id && g.Fence == lease.Fence);
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_X3_census_purge_is_refused_when_the_state_moved_between_the_census_and_the_purge()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        kit.Data.Native = 4;
        kit.Data.AfterFirstCount = async () => await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateKit.Expecting(ready), StateKit.TargetFrom(ready, "S3", "fwd", 1));

        await kit.StepAsync();

        kit.Data.Calls.Should().NotContain("purge_n");
        kit.Alerts.Should().Contain(a => a.Code == "state_moved");
    }

    [Fact]
    public async Task The_X10_sweep_of_N_is_refused_under_a_stale_fence()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var (lease, key) = await LeaseAsync(kit);
        kit.Fence = lease.Fence;
        await ToReverseDrainedAsync(kit);
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "N residue acknowledged", "op", CancellationToken.None);
        kit.Fence = new LeaseFence(key, "stale");

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        kit.Data.Calls.Should().NotContain("purge_n");
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S4" && s.Direction == "rev");
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_X11_purge_after_the_flip_is_refused_under_a_stale_fence()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.AcceptRefill = true;
        var (lease, key) = await LeaseAsync(kit);
        kit.Fence = lease.Fence;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        await kit.StepUntilAsync("S4");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);
        await kit.StepUntilAsync("S0", "none");
        kit.Fleet.All(n => n.Recycles = true);
        kit.Fence = new LeaseFence(key, "stale");

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        kit.Data.Calls.Should().NotContain("purge_l").And.NotContain("purge_n");
        (await kit.StateAsync()).Field(StateFields.ColdStart).Should().NotBeEmpty();
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_X11_legacy_purge_checks_the_guard_again_so_a_lease_taken_over_during_the_native_purge_stops_it()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.AcceptRefill = true;
        var (lease, key) = await LeaseAsync(kit);
        kit.Fence = lease.Fence;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        await kit.StepUntilAsync("S4");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);
        await kit.StepUntilAsync("S0", "none");
        kit.Fleet.All(n => n.Recycles = true);
        kit.Data.AfterNativePurge = async () =>
        {
            await kit.Kit.Db.KeyDeleteAsync(key);
            await new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time).TryHoldAsync();
        };

        await kit.StepAsync();

        kit.Data.Calls.Should().Contain("purge_n").And.NotContain("purge_l");
        kit.Alerts.Should().Contain(a => a.Code == "lease_lost");
        (await kit.StateAsync()).Field(StateFields.ColdStart).Should().NotBeEmpty();
        await kit.Kit.Db.KeyDeleteAsync(key);
        lease.Token.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_lost_post_write_of_X9_blocks_the_release_until_a_complete_census_is_written_under_the_identity()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToReverseMovingAsync(kit);
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        var reverse = await kit.StepUntilAsync("S4", "rev");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "no N residue", "op", CancellationToken.None);
        await kit.Kit.Db.HashDeleteAsync(kit.Kit.StateKey, StateFields.ResidueId);
        kit.Mover.Report = null;

        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S4", "no census, no release");
        kit.Alerts.Should().Contain(("stage_pending", "release_census_pending"));

        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        await kit.StepAsync();
        (await kit.StateAsync()).Field(StateFields.ResidueId).Should().Be(reverse.Id);
        await kit.StepUntilAsync("S0", "none");
    }

    [Fact]
    public async Task A_resumed_X5_checks_the_views_again_before_step_4_and_is_abandoned_when_one_fell()
    {
        var options = new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50), EvictionPolicyAttested = true };
        await using var kit = await DriverKit.CreateAsync(fixture, state: options);
        kit.Kit.Topology.Probe = _ => new MasterProbe(1, "noeviction");
        kit.Kit.Topology.Wait = (_, _) => 1;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        var drained = await kit.StepUntilAsync("S4");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        kit.Kit.Topology.Wait = (_, _) => 0;
        for (var n = 0; n < 8 && (await kit.StateAsync()).Durable; n++)
        {
            await kit.StepAsync(TimeSpan.FromSeconds(30));
        }

        var stuck = await kit.StateAsync();
        stuck.Should().Match<StateRecord>(s => s.State == "S5" && !s.Durable);

        // The planner still reads 5 everywhere; a view falls while the resumed sequence runs its barriers.
        kit.Kit.Topology.Wait = (_, _) => 1;
        kit.Fleet.AfterReads(1, () => kit.Fleet.Nodes[1].Fl = 4);
        var outcome = await kit.StepAsync(Ten);

        outcome!.Plan.Kind.Should().Be(StepKind.Resume);
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S4" && s.Durable && s.Ep > stuck.Ep);
        after.Field(StateFields.ClosedAt).Should().BeEmpty();
        drained.State.Should().Be("S4");
    }

    [Fact]
    public async Task The_reverse_census_replaces_the_forward_residue_and_a_release_consent_dies_when_the_census_changes()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToReverseMovingAsync(kit);
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 3));

        var reverse = await kit.StepUntilAsync("S4", "rev");

        reverse.Residue!["a"].Should().Be(3, "the forward count was 0");
        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "three N keys acknowledged", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 5));

        await kit.StepAsync();

        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S4" && s.Residue!["a"] == 5);
        await kit.StepAsync();
        (await kit.StateAsync()).State.Should().Be("S4", "the consent covered the count of 3");
        kit.Alerts.Should().Contain(("stage_pending", "release_consent"));
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "five N keys acknowledged", "op", CancellationToken.None);
        await kit.StepUntilAsync("S0", "none");
    }

    [Fact]
    public async Task The_automatic_hold_and_the_field_writes_of_the_leader_are_refused_under_a_stale_fence()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var (lease, key) = await LeaseAsync(kit);
        kit.Fence = lease.Fence;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(canary: 6, ("a", 0));
        kit.Fence = new LeaseFence(key, "stale");

        await kit.StepAsync();
        await kit.StepAsync();

        var refused = await kit.StateAsync();
        refused.HoldAuto.Should().BeEmpty("the hold is a leader write and the server refuses it");
        refused.Field(StateFields.CanaryAt).Should().BeEmpty();

        kit.Fence = lease.Fence;
        await kit.StepAsync();
        await kit.StepAsync();
        var done = await kit.StateAsync();
        done.HoldAuto.Should().Be("canary");
        done.Field(StateFields.CanaryAt).Should().NotBeEmpty();
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task The_loop_goes_on_after_any_exception_and_raises_step_failed()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Fleet.Fail = new InvalidOperationException("the node hash is unreadable");
        kit.Driver.Start([kit.Group], CancellationToken.None);
        try
        {
            await TimeSteps.UntilAsync(() => kit.Alerts.Contains(("step_failed", "the node hash is unreadable")));
            kit.Fleet.Fail = null;

            await TimeSteps.UntilAsync(() => kit.Kit.Store.ReadAsync(kit.Group, CancellationToken.None).GetAwaiter().GetResult()!.State == "S1" || Advance(kit));
        }
        finally
        {
            await kit.Driver.StopAsync();
        }
    }

    private static bool Advance(DriverKit kit)
    {
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(2));
        return false;
    }

    private static async Task<(LeaderLease Lease, RedisKey Key)> LeaseAsync(DriverKit kit)
    {
        var key = kit.Kit.Names.Lead(kit.Group);
        var lease = new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time);
        (await lease.TryHoldAsync()).Should().BeTrue();
        return (lease, key);
    }

    private static async Task ToReverseMovingAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        await kit.StepUntilAsync("S4");
        kit.Fleet.All(n => n.Fl = 2);
        await kit.StepUntilAsync("S1", "rev", advance: Ten);
        await kit.StepUntilAsync("S3", "rev");
    }

    private static async Task ToReverseDrainedAsync(DriverKit kit)
    {
        await ToReverseMovingAsync(kit);
        await kit.StepUntilAsync("S4", "rev");
    }
}
