using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The two-server transition on two real servers, with a failure injected at each step on each side.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceTransitionSequenceTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task A_transition_writes_the_dst_records_first_and_the_src_record_last_and_completes_on_both_servers()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Should().Equal("dst:transit", "dst:ctlsync", "src:ctlsync", "dst:cmt", "dst:ctlcommit", "src:ctlcommit", "dst:durable");
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
        (await kit.RecordAsync(RecordSide.Dst))["dur"].Should().Be("1");
        kit.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dst_that_never_confirms_the_transit_waits_and_writes_no_record_anywhere()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstReplicaFree: false));
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Reports.Select(r => r.Kind).Should().Contain(["barrier_stuck", "server_unreachable"]);
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse();
        (await kit.RawDst.KeyExistsAsync(kit.DstRecord)).Should().BeFalse();
        (await kit.ReadStateAsync())!.Durable.Should().BeFalse("the transit applied and is resumed from the stored state");
    }

    [Fact]
    public async Task An_exec_that_lost_its_reply_on_the_transit_is_never_sent_again_and_the_state_is_read_back()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "TRANSIT", TwoServerKit.CutMode.Lost);
        kit.Cuts.Add(cut);
        var target = TwoServerKit.Target(seeded);

        var result = await kit.RunAsync(seeded, target);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        cut.Fired.Should().Be(1, "an uncertain EXEC is not replayed");
        kit.Trace.Count(t => t == "dst:transit").Should().Be(1);
        (await kit.ReadStateAsync())!.Id.Should().Be(target.Id, "the write applied");
        kit.Reports.Should().Contain(r => r.Kind == "barrier_stuck" && r.Detail.Contains("uncertain (applied)", StringComparison.Ordinal));
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse();
    }

    [Fact]
    public async Task A_dst_record_write_that_fails_stops_before_the_src_record_is_written()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "CTLSYNC", TwoServerKit.CutMode.Down));

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse("the Src record is last");
        kit.Trace.Should().NotContain("src:ctlsync");
    }

    [Fact]
    public async Task A_src_record_write_that_fails_leaves_the_dst_records_and_a_resume_completes_the_transition()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Src, "CTLSYNC", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        var target = TwoServerKit.Target(seeded);

        (await kit.RunAsync(seeded, target)).Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.RecordAsync(RecordSide.Dst))["id"].Should().Be(target.Id);
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse();

        cut.Lift();
        var resumed = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        resumed.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_src_commit_that_fails_after_the_marker_keeps_the_state_undurable_refuses_the_abandon_and_a_resume_completes()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Src, "CTLCOMMIT", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        var state = (await kit.ReadStateAsync())!;
        state.Durable.Should().BeFalse("DURABLE needs both servers (INV-PerServerCtl)");
        state.Field(StateFields.CommitBegun).Should().Be(state.Id);
        (await kit.RecordAsync(RecordSide.Dst))["dur"].Should().Be("1", "one server alone committed");
        kit.Trace.Should().NotContain("dst:durable");

        (await kit.Sequence.AbandonAsync(kit.Group, false, 3, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.AbandonRefused);

        cut.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
    }

    [Fact]
    public async Task An_abandon_before_the_marker_goes_back_to_the_previous_state_on_both_servers()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Src, "CTLSYNC", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded))).Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        cut.Lift();

        var result = await kit.Sequence.AbandonAsync(kit.Group, false, 3, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.ReadStateAsync())!;
        (state.State, state.Direction, state.Durable).Should().Be(("S0", "none", true));
        (await kit.RecordAsync(RecordSide.Src))["id"].Should().Be(state.Id);
        (await kit.RecordAsync(RecordSide.Dst))["id"].Should().Be(state.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_abandon_is_refused_when_a_record_of_either_server_shows_dur_1_at_the_ep(bool onSrc)
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Src, "CTLSYNC", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        var target = TwoServerKit.Target(seeded);
        (await kit.RunAsync(seeded, target)).Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        cut.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, new SequenceOptions(BeforeCommit: _ => Task.FromResult<TransitionResult?>(new TransitionResult(TransitionOutcome.BarrierStuck))), CancellationToken.None))
            .Outcome.Should().Be(TransitionOutcome.BarrierStuck);

        // Both servers hold the target at dur=0 and there is no marker; one record is committed by hand.
        await (onSrc ? kit.RawSrc : kit.RawDst).HashSetAsync(onSrc ? kit.SrcRecord : kit.DstRecord, "dur", "1");

        var result = await kit.Sequence.AbandonAsync(kit.Group, false, 3, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.AbandonRefused);
        (await kit.ReadStateAsync())!.Id.Should().Be(target.Id, "nothing was written");
    }

    [Fact]
    public async Task A_resume_of_a_durable_state_repeats_durable_and_completes()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded))).Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Clear();

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Should().Equal("dst:durable");
    }

    [Fact]
    public async Task A_src_record_at_the_same_ep_under_another_id_is_an_identity_conflict_and_the_transition_goes_no_further()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var target = TwoServerKit.Target(seeded);
        await kit.RawSrc.HashSetAsync(kit.SrcRecord, [new("ep", target.Ep), new("id", "other"), new("dur", "0")]);

        var result = await kit.RunAsync(seeded, target);

        result.Outcome.Should().Be(TransitionOutcome.IdentityConflict);
        kit.Trace.Should().NotContain("dst:cmt");
    }

    [Fact]
    public async Task A_src_record_at_a_higher_ep_is_an_identity_conflict_not_a_silent_resume()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var target = TwoServerKit.Target(seeded);
        await kit.RawSrc.HashSetAsync(kit.SrcRecord, [new("ep", target.Ep + 5), new("id", "newer"), new("dur", "1")]);

        var result = await kit.RunAsync(seeded, target);

        result.Outcome.Should().Be(TransitionOutcome.IdentityConflict);
    }

    [Fact]
    public async Task A_connection_event_after_the_records_were_written_verifies_both_servers_again_before_the_claim_opens()
    {
        await using var quiet = await TwoServerKit.CreateAsync(fixture);
        var quietSeed = await quiet.SeedAsync();
        (await quiet.RunAsync(quietSeed, TwoServerKit.Target(quietSeed))).Outcome.Should().Be(TransitionOutcome.Completed);
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.OnExec = exec =>
        {
            if (exec == "src:ctlsync")
            {
                kit.Views.Changes.Observe(RecordSide.Dst);
            }
        };

        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded))).Outcome.Should().Be(TransitionOutcome.Completed);

        kit.VerifyReads.Should().BeGreaterThan(quiet.VerifyReads, "a change on either side forces a second full verification");
    }

    [Fact]
    public async Task A_controller_that_lost_its_lease_writes_nothing()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded), new SequenceOptions(HoldsLease: () => false));

        result.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        kit.Trace.Should().BeEmpty();
    }

    [Fact]
    public async Task The_condition_form_of_a_side_without_scripts_runs_the_same_transition()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(SrcScripts: false, DstScripts: false));
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
        (await kit.RecordAsync(RecordSide.Dst))["dur"].Should().Be("1");
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_resume_after_the_marker_completes_even_when_the_dst_has_fewer_replicas_than_expected()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Src, "CTLCOMMIT", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded))).Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        cut.Lift();

        // The resume expects a replica the Dst does not have: before the marker dst_preflight would stop it, from the marker on the completion never aborts.
        var options = new KeyMoveStateOptions { BarrierRetries = 2, BarrierWait = TimeSpan.FromSeconds(2), ExpectedReplicasPerMaster = 1 };
        var reports = new List<string>();
        var sequence = new InstanceTransitionSequence(kit.Placement, kit.Conns, kit.DstState, kit.SrcWriter, _ => kit.DstMaster, kit.Views, kit.Generations, options, (kind, _) => reports.Add(kind));
        (await sequence.DstPreflightAsync(kit.Group, CancellationToken.None)).Ok.Should().BeFalse("the Dst has no replica");

        (await sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);

        reports.Should().NotContain("dst_preflight");
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
    }

    [Fact]
    public async Task A_dst_with_fewer_replicas_than_expected_fails_the_preflight_and_nothing_is_written_to_the_src()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(ExpectedReplicas: 1));
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        result.Detail.Should().Contain("dst_preflight");
        kit.Reports.Should().Contain(r => r.Kind == "dst_preflight");
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse();
        kit.Trace.Should().NotContain("src:ctlsync");
    }

    [Fact]
    public async Task A_dst_that_passes_the_preflight_lets_the_src_write_follow()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();

        (await kit.Sequence.DstPreflightAsync(kit.Group, CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_real_replica_on_the_src_confirms_every_write_and_nothing_is_skipped()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(UseReplicaSrc: true, SrcReplicaFree: false));
        var seeded = await kit.SeedAsync();

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        result.WaitSkipped.Should().BeFalse();
    }
}
