using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The review fixes of the two-server transition: repairs follow the failure policy, DURABLE is complete only when proven, and a resume keeps the commit marker.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceTransitionSequenceReviewTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task A_dst_repair_that_does_not_confirm_before_the_marker_stops_the_transition_even_though_a_master_read_shows_it()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.OnExec = exec =>
        {
            if (exec == "src:ctlsync")
            {
                // The Dst record is lost after its write: barrier 1 finds it missing and repairs it.
                kit.RawDst.KeyDelete(kit.DstRecord);
            }
        };
        var repair = new TwoServerKit.Cut(RecordSide.Dst, "CTLSYNC", TwoServerKit.CutMode.Lost, times: 1, skip: 1);
        kit.Cuts.Add(repair);

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        repair.Fired.Should().Be(1);
        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.RecordAsync(RecordSide.Dst)).Should().ContainKey("id", "the repair applied, so the next master read would show it");
        kit.Trace.Should().NotContain("dst:cmt", "the marker is never written on an unconfirmed repair");
        kit.Reports.Should().Contain(r => r.Kind == "barrier_stuck" && r.Detail.Contains("repair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dst_preflight_runs_again_before_a_src_repair_and_a_failure_writes_nothing_to_the_src()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.OnVerify = read =>
        {
            if (read == 1)
            {
                // Both records are lost after their writes, so barrier 1 repairs the Dst first and the Src last.
                kit.RawDst.KeyDelete(kit.DstRecord);
                kit.RawSrc.KeyDelete(kit.SrcRecord);
            }
        };
        var dstRepairs = 0;
        kit.OnExec = exec =>
        {
            if (exec == "dst:ctlsync" && ++dstRepairs == 2)
            {
                // The Dst repair is on its way: the state the preflight reads goes away with it.
                kit.RawDst.KeyDelete(kit.StateKey);
            }
        };

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        result.Detail.Should().Contain("dst_preflight");
        kit.Trace.Count(t => t == "src:ctlsync").Should().Be(1, "the Src repair was never sent");
        (await kit.RawSrc.KeyExistsAsync(kit.SrcRecord)).Should().BeFalse();
    }

    [Fact]
    public async Task A_durable_write_that_never_applies_is_not_completed_and_a_resume_completes_it()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "DURABLE", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        (await kit.ReadStateAsync())!.Durable.Should().BeFalse("nothing was written");

        cut.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_durable_write_that_applied_with_a_lost_reply_is_read_back_with_its_release_certificate_and_completes_as_wait_skipped()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "DURABLE", TwoServerKit.CutMode.Lost));

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded), new SequenceOptions(Release: "cert-1"));

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        result.WaitSkipped.Should().BeTrue("the write is proven by the read-back, not by a WAIT");
        var state = (await kit.ReadStateAsync())!;
        state.Durable.Should().BeTrue();
        state.Release.Should().Be("cert-1");
    }

    [Fact]
    public async Task A_resume_starts_past_the_stored_commit_marker_so_an_unconfirmed_write_is_skipped_not_stuck()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstReplicaFree: false));
        var seeded = await kit.SeedAsync();
        var target = TwoServerKit.Target(seeded);
        var stop = new SequenceOptions(SkipBarriers: true, BeforeCommit: _ => Task.FromResult<TransitionResult?>(new TransitionResult(TransitionOutcome.BarrierStuck)));
        (await kit.RunAsync(seeded, target, stop)).Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        await kit.RawDst.HashSetAsync(kit.StateKey, StateFields.CommitBegun, target.Id);

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed, "the identity crossed the no-abandon boundary, so completion never waits for a replica");
        result.WaitSkipped.Should().BeTrue();
        kit.Reports.Should().Contain(r => r.Kind == "wait_skipped");
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1");
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }
}
