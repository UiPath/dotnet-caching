using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class TransitionSequenceTests(RedisContainerFixture fixture)
{
    private static readonly PhaseBStateOptions WithReplica = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50) };

    public static TheoryData<bool, string> Resumes()
    {
        var data = new TheoryData<bool, string>();
        foreach (var useScripts in new[] { true, false })
        {
            foreach (var step in new[] { "transit", "epsync", "commit", "durable" })
            {
                data.Add(useScripts, step);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_writes_the_state_the_control_record_and_both_commit_markers(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, new PhaseBStateOptions { ExpectedReplicasPerMaster = 0 });
        var init = await kit.InitAsync("a", "b");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.Ep == target.Ep && s.Id == target.Id && s.Durable && s.State == "S1" && s.Direction == "fwd" && s.NPop == 0);
        state.Prev.Should().Be($"S1:fwd:0:{target.Ep}:{target.Id}");
        var record = (await kit.ControlAsync(0)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
        record.Should().Contain("ep", target.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.Contain("id", target.Id).And.Contain("dur", "1");
        record.Should().Contain("st", "S1").And.Contain("dir", "fwd").And.Contain("pst", "S0").And.Contain("pid", init.Id);
        record.Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Should().BeEquivalentTo("c:a", "c:b");
        record["c:a"].Should().StartWith("OPEN:");
        kit.Reports.Should().ContainSingle().Which.Kind.Should().Be("no_replica");
    }

    [Theory]
    [MemberData(nameof(Resumes))]
    public async Task A_run_that_stopped_after_any_step_is_resumed_to_the_same_end_state(bool useScripts, string step)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S3", "fwd", 1);
        (await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target)).Should().Be(MoveScripts.Written);
        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var control = kit.Names.Control(kit.Group, 0);
        if (step is "epsync" or "commit" or "durable")
        {
            (await kit.Scripts.ControlSyncAsync(control, stored.ToControlRecord([("a", "n-before")]))).Should().Be(MoveScripts.Written);
        }

        if (step is "commit" or "durable")
        {
            (await kit.Scripts.ControlCommitAsync(control, stored.Ep, stored.Id)).Should().Be(MoveScripts.Written);
        }

        if (step == "durable")
        {
            (await kit.Scripts.DurableAsync(kit.StateKey, stored.Id)).Should().Be(MoveScripts.Written);
        }

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
        var record = (await kit.ControlAsync(0)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
        record.Should().Contain("id", target.Id).And.Contain("dur", "1").And.Contain("np", "1");
        record["c:a"].Should().StartWith("OPEN:");
        if (step != "transit")
        {
            record["c:a"].Should().Be("OPEN:n-before", "an EPSYNC repeat never gives the gate a new nonce");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_over_a_state_that_is_not_the_expected_one_changes_nothing(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        var result = await kit.Sequence.RunAsync(kit.Group, StateExpectation.Holding(init.Ep, "other", 1), target, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.StateChanged);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(init.Id);
        (await kit.ControlAsync(0)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resume_of_a_state_that_is_already_durable_or_absent_does_nothing(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);

        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.StateChanged);
        await kit.InitAsync("a");
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ControlAsync(0)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_control_record_of_the_same_epoch_under_another_id_or_of_a_higher_epoch_is_an_identity_conflict(bool useScripts, bool higher)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), MoveTestEnvironment.Record(higher ? target.Ep + 1 : target.Ep, "intruder", ("a", "n")));

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.IdentityConflict);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_low_count_is_tried_again_on_a_replaced_connection_until_it_confirms(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");

        // The state write confirms; the control records are short twice, then confirm.
        kit.Topology.Wait = (_, n) => n is 2 or 3 ? 0 : 1;
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Opened.Should().Be(3, "each failed count replaces the connection");
        kit.Reports.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_barrier_before_step_four_that_never_confirms_waits_at_dur_zero_and_a_resume_finishes_it(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, n) => n == 1 ? 1 : 0;

        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Topology.Waits.Should().Be(1 + WithReplica.BarrierRetries);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
        (await kit.ControlAsync(0)).Should().NotContain(e => e.Name == "dur" && e.Value == "1");
        kit.Reports.Select(r => r.Kind).Should().Contain("barrier_stuck");

        kit.Topology.Wait = null;
        kit.Topology.Wait = (_, _) => 1;
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_skipped_barrier_lets_the_transition_go_on_and_says_so(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
        kit.Reports.Select(r => r.Kind).Should().Contain("wait_skipped");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task From_step_four_on_a_barrier_that_fails_proceeds_with_wait_skipped(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var retries = WithReplica.BarrierRetries;

        // TRANSIT, barrier 1 and the commit marker confirm at once; barrier 2 and the state WAIT fail in every try.
        kit.Topology.Wait = (_, n) => n <= 3 ? 1 : 0;
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(3 + (2 * retries));
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
        kit.Reports.Count(r => r.Kind == "wait_skipped").Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_that_vanishes_between_the_barrier_and_the_verification_is_issued_again(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var control = kit.Names.Control(kit.Group, 0);

        // The record is lost (an eviction, a failover) right after the first barrier on the control record.
        kit.Topology.Wait = (_, n) =>
        {
            if (n == 2)
            {
                kit.Db.KeyDelete(control);
            }

            return 1;
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_that_is_never_there_after_the_barrier_is_stuck_even_when_barriers_may_be_skipped(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var control = kit.Names.Control(kit.Group, 0);
        kit.Topology.Wait = (_, n) =>
        {
            if (n > 1)
            {
                kit.Db.KeyDelete(control);
            }

            return 1;
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_commit_that_a_slot_refuses_is_redone_from_the_epoch_sync(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var control = kit.Names.Control(kit.Group, 0);

        // The record vanishes after the epoch sync confirmed; its commit finds nothing (0), so the sequence runs the sync again.
        var deleted = false;
        kit.Topology.Wait = (_, n) =>
        {
            if (n == 4 && !deleted)
            {
                deleted = true;
                kit.Db.KeyDelete(control);
            }

            return 1;
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_controllers_racing_for_the_same_expected_state_leave_one_winner(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");

        var codes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0))));

        codes.Count(c => c == MoveScripts.Written).Should().Be(1);
        codes.Count(c => c == MoveScripts.ExpectationFailed).Should().Be(7);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_controller_that_lost_the_race_to_a_newer_state_is_told_the_state_changed(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var winner = StateKit.TargetFrom(init, "S1", "fwd", 0);
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), winner, null, CancellationToken.None);

        var late = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        late.Outcome.Should().Be(TransitionOutcome.StateChanged);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(winner.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_standalone_connection_that_is_not_told_its_replicas_refuses_a_barrier_and_writes_nothing(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, new PhaseBStateOptions());
        var init = await kit.InitAsync("a");

        (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Should().ContainSingle().Which.ConfiguredReplicas.Should().Be(MasterLayout.UnknownReplicas);
        var act = () => kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(init.Id);
        (await kit.ControlAsync(0)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_replica_count_of_zero_is_a_failed_barrier_when_one_replica_is_configured(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Reports.Select(r => r.Kind).Should().NotContain("no_replica");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public async Task A_resume_of_a_durable_state_repeats_the_durable_write_and_its_wait(bool useScripts, long acked)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        var waitsBefore = kit.Topology.Waits;
        kit.Topology.Wait = (_, _) => acked;
        await using var probe = await WriteProbe.CreateAsync(fixture.ConnectionString);
        await probe.WatchAsync(kit.StateKey);

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        (await probe.TouchedAsync()).Should().BeTrue("DURABLE is written again so the WAIT has an offset");
        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: acked == 0));
        kit.Topology.Waits.Should().Be(waitsBefore + (acked == 0 ? WithReplica.BarrierRetries : 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_whose_durable_reply_was_lost_is_waited_for_on_resume(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S3", "fwd", 1);
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, stored.ToControlRecord([("a", "n")]));
        await kit.Scripts.ControlCommitAsync(control, stored.Ep, stored.Id);
        await kit.Scripts.DurableAsync(kit.StateKey, stored.Id);
        kit.Topology.Wait = (_, _) => 1;

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Topology.Waits.Should().Be(1);
    }
}
