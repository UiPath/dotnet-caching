using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The admission baselines on two servers: the baselines the claim steps write, the gates that read them from any node, and the rule that an absent claim never records one.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class AdmissionRecordingClaimTests(TwoServerFixture fixture)
{
    private const string Token = InstanceRecoveryKit.Token;
    private const string TokenB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task Pend_writes_the_dst_baseline_before_the_mirror_pend_and_the_src_baseline_after_the_src_claim_is_read_present()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);

        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull("written before the mirror pend");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull("written and confirmed before any Src pend, which a Src failover could lose before a read");

        cut.Lift();
        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        var src = await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken);
        src!.Token.Should().Be(Token);
        src.Generation.Should().Be((await kit.Generations.ReadAsync(RecordSide.Src, TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public async Task A_dst_generation_that_cannot_be_read_stops_the_pend_before_anything_is_written()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        kit.Generations.Unknown(RecordSide.Dst, true);

        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        (await kit.RawDst.HashGetAllAsync(rk.DstClaimKey)).Should().BeEmpty();
        (await kit.RawSrc.HashGetAllAsync(rk.SrcClaimKey)).Should().BeEmpty();
        rk.Alerts.Should().Contain(a => a.Code == "baseline_unavailable");
    }

    [Fact]
    public async Task A_dst_failover_after_the_pend_closes_the_dst_gate_for_a_fresh_pod_until_the_confirmed_open_moves_the_baseline()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Generations.Change(RecordSide.Dst);

        // A pod that was never part of the leader: its own baseline store over the same Dst.
        var gate = new DstAdmissionGate(kit.Generations, new RedisAdmissionBaselines(kit.RawDst, kit.Placement.Names(RecordSide.Dst), new MoveScripts(kit.RawDst, true, kit.DstCaps.Version)), (a, c, d) => rk.Alerts.Add((a, c, d)));
        var refused = await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken);

        refused.Admit.Should().BeFalse();
        rk.Alerts.Should().Contain(a => a.Code == "mirror_repair_pending");

        (await rk.Steps.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue("the baseline follows a confirmed write in the new generation");
    }

    [Fact]
    public async Task An_open_whose_dst_generation_moved_during_the_step_leaves_the_baseline_where_it_was()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var held = await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken);
        var reads = 0;
        kit.Generations.Rewrite = (side, _, actual) => side == RecordSide.Dst && Interlocked.Increment(ref reads) > 2 && actual is { } g ? g with { ReplicationId = "moved" } : actual;

        var outcome = await rk.Steps.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken);

        outcome.Should().NotBe(ClaimOutcome.Done);
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(held);
    }

    [Fact]
    public async Task A_claim_read_that_finds_the_src_field_absent_never_records_or_refreshes_a_baseline()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var before = await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken);
        kit.Generations.Change(RecordSide.Src);
        await kit.RawSrc.KeyDeleteAsync(rk.SrcClaimKey);

        (await rk.Steps.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(before);
        var gate = new SrcAdmissionGate(() => true, kit.Generations, rk.Baselines, _ => rk.DstClaim.ReadAsync(), (a, c, d) => rk.Alerts.Add((a, c, d)));
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("the mirror still shows the move");
        rk.Alerts.Should().Contain(a => a.Code == "src_admission_pending");
    }

    [Fact]
    public async Task A_src_claim_read_that_shows_the_field_absent_after_the_pend_records_no_src_baseline()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        var blind = new AdmissionRecordingClaim(rk.Claim, TwoServerKit.App, kit.Generations, rk.Baselines, _ => Task.FromResult(ClaimRecord.From([])), (g, c, d) => rk.Alerts.Add((g, c, d)), rk.Tokens);

        (await blind.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed, "a claim that is not read present is not confirmed");

        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Token.Should().Be(Token, "the baseline stands from before the pend; an absent read neither records nor removes it");
        rk.Alerts.Should().Contain(a => a.Code == "baseline_unavailable");
    }

    [Fact]
    public async Task A_retry_never_moves_the_dst_baseline_to_a_generation_whose_mirror_is_not_repaired_and_a_confirmed_repair_does()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var first = (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!;
        kit.Generations.Change(RecordSide.Dst);
        await kit.RawDst.KeyDeleteAsync(rk.DstClaimKey);
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        var gate = new DstAdmissionGate(kit.Generations, rk.Baselines);

        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().NotBe(ClaimOutcome.Done);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(first, "the retry did not repair the mirror, so the baseline stays");
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();

        cut.Lift();
        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Generation.Should().NotBe(first.Generation, "the mirror pend was confirmed in the new generation");
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
    }

    [Fact]
    public async Task A_src_pend_that_applies_and_is_lost_with_the_src_claim_leaves_the_baseline_and_the_src_gate_refuses_on_the_mirror()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Generations.Change(RecordSide.Src);
        await kit.RawSrc.KeyDeleteAsync(rk.SrcClaimKey);
        // Even with the baseline lost as well, the gate reads the live mirror and refuses.
        await rk.Baselines.ClearAsync(TwoServerKit.App, Token, TestContext.Current.CancellationToken);
        var gate = new SrcAdmissionGate(() => true, kit.Generations, rk.Baselines, _ => rk.DstClaim.ReadAsync());

        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("no baseline, and the mirror shows the move");
    }

    [Fact]
    public async Task Settling_the_move_removes_both_baselines_and_both_gates_pass()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        await rk.Steps.CertifyAsync(kit.Group, Token, moved.Ep, "S0", null, TestContext.Current.CancellationToken);

        (await rk.Steps.ReleaseIdleAsync(kit.Group, Token, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
        kit.Generations.Change(RecordSide.Dst);
        kit.Generations.Change(RecordSide.Src);
        (await new DstAdmissionGate(kit.Generations, rk.Baselines).CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
        (await new SrcAdmissionGate(() => true, kit.Generations, rk.Baselines, _ => rk.DstClaim.ReadAsync()).CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
    }

    [Fact]
    public async Task A_src_failover_that_lost_the_claim_is_refused_until_the_mirror_shows_the_move_ended()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        kit.Generations.Change(RecordSide.Src);
        await kit.RawSrc.KeyDeleteAsync(rk.SrcClaimKey);
        var gate = new SrcAdmissionGate(() => true, kit.Generations, rk.Baselines, _ => rk.DstClaim.ReadAsync());

        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("a fresh OPEN mirror means move");

        await rk.DstClaim.CertifyAsync("imd:" + kit.Group, Token, moved.Ep, swept: false);
        await rk.DstClaim.IdleAsync("imd:" + kit.Group, Token);

        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue("a fresh idle S0 certificate of this token is clear");
    }

    [Fact]
    public async Task A_stale_pend_paused_before_it_reads_the_baseline_cannot_replace_the_baseline_of_the_acquisition_that_took_over()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Tokens.WriteAsync(kit.Group, Token, null, TestContext.Current.CancellationToken);
        var paused = new PausingBaselines(rk.Baselines);
        var stale = new AdmissionRecordingClaim(rk.Claim, TwoServerKit.App, kit.Generations, paused, _ => rk.SrcClaim.ReadAsync(), (g, c, d) => rk.Alerts.Add((g, c, d)), rk.Tokens);
        AdmissionBaseline? taken = null;
        paused.SetBeforeFirstRead(async () =>
        {
            taken = await TakeOverAsync(rk, TokenB);
        });

        (await stale.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        await AssertTakeOverHeldAsync(rk, taken!);
    }

    [Fact]
    public async Task A_stale_pend_paused_after_its_ownership_check_cannot_write_over_a_baseline_the_new_acquisition_wrote_since()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        await rk.Tokens.WriteAsync(kit.Group, Token, null, TestContext.Current.CancellationToken);
        var paused = new PausingBaselines(rk.Baselines);
        var stale = new AdmissionRecordingClaim(rk.Claim, TwoServerKit.App, kit.Generations, paused, _ => rk.SrcClaim.ReadAsync(), (g, c, d) => rk.Alerts.Add((g, c, d)), rk.Tokens);
        AdmissionBaseline? taken = null;
        paused.SetBeforeFirstWrite(async () =>
        {
            taken = await TakeOverAsync(rk, TokenB);
        });

        (await stale.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        await AssertTakeOverHeldAsync(rk, taken!);
    }

    [Fact]
    public async Task With_no_acquisition_stored_a_baseline_of_another_token_stands_and_a_lease_that_is_not_held_writes_nothing()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();
        var generation = (await kit.Generations.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken))!.Value;
        var other = new AdmissionBaseline(generation, TokenB);
        await rk.SeedBaselineAsync("dst", other);

        (await rk.Steps.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed, "pt names no acquisition, so a live baseline of another token is not replaced");

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(other);
        await kit.RawDst.KeyDeleteAsync(kit.Placement.Names(RecordSide.Dst).Baseline(TwoServerKit.App, "dst"));
        await rk.Tokens.WriteAsync(kit.Group, Token, null, TestContext.Current.CancellationToken);
        var lost = new LeaseFence(kit.Placement.Names(RecordSide.Dst).Lead(kit.Group), "not-the-holder");

        (await rk.Steps.PendAsync(kit.Group, Token, "inst", lost, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull("a caller that lost the lease writes no baseline");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task An_absent_value_race_leaves_an_orphan_baseline_that_a_recovery_refresh_removes_so_the_dst_admits_again()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        var ct = TestContext.Current.CancellationToken;

        // A is a stale caller: no pt, an absent baseline read, and an inner claim that no longer holds the lease.
        var paused = new PausingBaselines(rk.Baselines);
        var lapsed = new InstanceMoveClaim(rk.SrcClaim, rk.DstClaim, kit.Generations, _ => false, null, rk.Tokens);
        var stale = new AdmissionRecordingClaim(lapsed, TwoServerKit.App, kit.Generations, paused, _ => rk.SrcClaim.ReadAsync(), (g, c, d) => rk.Alerts.Add((g, c, d)), rk.Tokens);
        paused.SetBeforeFirstWrite(async () =>
        {
            // B acquires, completes, and clears its baseline and its fields.
            await rk.Tokens.WriteAsync(kit.Group, TokenB, null, ct);
            (await rk.Steps.PendAsync(kit.Group, TokenB, "inst", null, ct)).Should().Be(ClaimOutcome.Done);
            await rk.Tokens.ClearAsync(kit.Group, null, ct);
            await kit.RawSrc.HashDeleteAsync(rk.SrcClaimKey, "im:" + kit.Group);
            await kit.RawDst.HashDeleteAsync(rk.DstClaimKey, "imd:" + kit.Group);
            await rk.Baselines.ClearAsync(TwoServerKit.App, TokenB, ct);
        });

        (await stale.PendAsync(kit.Group, Token, "inst", null, ct)).Should().Be(ClaimOutcome.Failed);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, ct))!.Token.Should().Be(Token, "the expected-absent write succeeded after B cleared: an orphan");
        kit.Generations.Change(RecordSide.Dst);
        var gate = new DstAdmissionGate(kit.Generations, rk.Baselines);
        (await gate.CheckAsync(TwoServerKit.App, ct)).Admit.Should().BeFalse("the orphan refuses after a generation change");

        await rk.Recovery.RefreshAsync(kit.Group, seeded, ct);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, ct)).Should().BeNull();
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, ct)).Should().BeNull();
        (await gate.CheckAsync(TwoServerKit.App, ct)).Admit.Should().BeTrue();
    }

    // The new acquisition B settles A's token, stores its own, pends (baselines and mirror), and then a Dst promotion loses B's mirror.
    private static async Task<AdmissionBaseline> TakeOverAsync(InstanceRecoveryKit rk, string token)
    {
        var kit = rk.Kit;
        await rk.Tokens.ClearAsync(kit.Group, null, CancellationToken.None);
        await rk.Tokens.WriteAsync(kit.Group, token, null, CancellationToken.None);
        (await rk.Steps.PendAsync(kit.Group, token, "inst", null, CancellationToken.None)).Should().Be(ClaimOutcome.Done);
        var written = (await rk.Baselines.ReadDstAsync(TwoServerKit.App, CancellationToken.None))!;
        written.Token.Should().Be(token);
        await kit.RawDst.HashDeleteAsync(rk.DstClaimKey, "imd:" + kit.Group);
        kit.Generations.Change(RecordSide.Dst);
        return written;
    }

    private static async Task AssertTakeOverHeldAsync(InstanceRecoveryKit rk, AdmissionBaseline taken)
    {
        var kit = rk.Kit;
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(taken, "B's baseline survives unchanged");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Token.Should().Be(TokenB);
        (await rk.MirrorFieldAsync()).Should().BeNull("A's PEND wrote nothing");
        (await rk.SrcFieldAsync()).Should().StartWith("pend|" + TokenB);
        var refused = await new DstAdmissionGate(kit.Generations, rk.Baselines).CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken);
        refused.Admit.Should().BeFalse("the promoted Dst lost B's mirror and B's baseline still names the old generation");
        refused.Alert.Should().Be("mirror_repair_pending");
    }

    // A baseline store whose first read or first write waits for a test hook, to stop a caller at that point.
    private sealed class PausingBaselines(IAdmissionBaselines inner) : IAdmissionBaselines
    {
        private Func<Task>? _beforeRead;
        private Func<Task>? _beforeWrite;

        public void SetBeforeFirstRead(Func<Task> action) => _beforeRead = action;

        public void SetBeforeFirstWrite(Func<Task> action) => _beforeWrite = action;

        public async Task<AdmissionBaseline?> ReadDstAsync(string app, CancellationToken cancellationToken)
        {
            await RunAsync(Interlocked.Exchange(ref _beforeRead, null));
            return await inner.ReadDstAsync(app, cancellationToken);
        }

        public async Task<bool> WriteDstAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken)
        {
            await RunAsync(Interlocked.Exchange(ref _beforeWrite, null));
            return await inner.WriteDstAsync(app, baseline, replaces, cancellationToken);
        }

        public Task<AdmissionBaseline?> ReadSrcAsync(string app, CancellationToken cancellationToken) => inner.ReadSrcAsync(app, cancellationToken);

        public Task<bool> WriteSrcAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken) => inner.WriteSrcAsync(app, baseline, replaces, cancellationToken);

        public Task ClearAsync(string app, string token, CancellationToken cancellationToken) => inner.ClearAsync(app, token, cancellationToken);

        private static async Task RunAsync(Func<Task>? hook)
        {
            if (hook is not null)
            {
                await hook();
            }
        }
    }
}
