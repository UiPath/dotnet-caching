using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class BarrierSkipAndAbandonTests(RedisContainerFixture fixture)
{
    private static readonly KeyMoveStateOptions WithReplica = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50) };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stuck_transition_proceeds_with_wait_skipped_when_the_operator_skips_the_barrier(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, n) => n == 1 ? 1 : 0;
        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);

        var skipped = await kit.Sequence.ResumeAsync(kit.Group, new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        skipped.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Reports.Select(r => r.Kind).Should().Contain("wait_skipped");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Skipping_at_once_issues_the_writes_and_no_wait(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(SkipAtOnce: true), CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(0);
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_way_back_skips_a_master_that_already_failed_a_barrier_at_once_and_the_window_ends(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;
        var way = new SequenceOptions(WayBack: true, Ledger: kit.Ledger);

        // The first way-back transition tries the master its three times, then goes on; every later barrier of the episode is skipped at once.
        var first = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), way, CancellationToken.None);
        first.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(WithReplica.BarrierRetries);

        var second = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var again = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(second), StateKit.TargetFrom(second, "S0", "none", 0), way, CancellationToken.None);
        again.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(WithReplica.BarrierRetries, "the failed master is not waited for again");

        kit.Time.Advance(TimeSpan.FromMinutes(11));
        var third = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(third), StateKit.TargetFrom(third, "S1", "fwd", 0), way, CancellationToken.None);
        kit.Topology.Waits.Should().BeGreaterThan(WithReplica.BarrierRetries, "ten minutes later the master is tried again");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forward_transition_is_never_skipped_by_the_ledger(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(Ledger: kit.Ledger), CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Ledger.FailedRecently((await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters[0].Endpoint).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_abandon_before_step_four_returns_to_the_last_committed_state_with_the_barriers_skipped(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var stuckTarget = StateKit.TargetFrom(init, "S1", "fwd", 0);
        kit.Topology.Wait = (_, n) => n == 1 ? 1 : 0;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), stuckTarget, null, CancellationToken.None);
        var waits = kit.Topology.Waits;

        var result = await kit.Sequence.AbandonAsync(kit.Group, advertAcknowledges: false, updatedAt: 5, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(waits);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S0" && s.Direction == "none" && s.Durable && s.Ep == stuckTarget.Ep + 1 && s.Id != stuckTarget.Id && s.Id != init.Id);
        state.Prev.Should().Be($"S0:none:0:{state.Ep}:{state.Id}", "DURABLE records the committed state as prev");
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "pid" && e.Value == init.Id);
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "id" && e.Value == state.Id);
    }

    [Theory]
    [InlineData(true, "advert")]
    [InlineData(true, "record")]
    [InlineData(true, "durable")]
    [InlineData(true, "absent")]
    [InlineData(false, "advert")]
    [InlineData(false, "record")]
    [InlineData(false, "durable")]
    [InlineData(false, "absent")]
    public async Task Abandon_refuses_after_commit_or_acknowledgement_or_without_a_transition(bool useScripts, string why)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var before = await PrepareAbandonAsync(kit, why);

        var result = await kit.Sequence.AbandonAsync(kit.Group, advertAcknowledges: why == "advert", updatedAt: 5, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.AbandonRefused);
        if (before is not null)
        {
            (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.Identity == before.Identity && s.Durable == before.Durable);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_that_shows_the_commit_while_the_state_does_not_is_completed_never_abandoned(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, stored.ToControlRecord([("a", "n")]));
        await kit.Scripts.ControlCommitAsync(control, stored.Ep, stored.Id);

        var abandon = await kit.Sequence.AbandonAsync(kit.Group, false, 5, null, CancellationToken.None);
        var completed = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        abandon.Outcome.Should().Be(TransitionOutcome.AbandonRefused);
        completed.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.Id == target.Id && s.Durable);
    }

    private static async Task<StateRecord?> PrepareAbandonAsync(StateKit kit, string why)
    {
        StateRecord? before = null;
        if (why != "absent")
        {
            var init = await kit.InitAsync("a");
            var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
            await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
            var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            if (why == "record")
            {
                var control = kit.Names.Control(kit.Group, 0);
                await kit.Scripts.ControlSyncAsync(control, stored.ToControlRecord([("a", "n")]));
                await kit.Scripts.ControlCommitAsync(control, stored.Ep, stored.Id);
            }

            if (why == "durable")
            {
                await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);
            }

            before = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        }

        return before;
    }
}
