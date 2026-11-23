using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>A Src flush and its Rebuild, a Dst flush and Reassert, a claim that lost its OPEN, a cut in the middle of a transition on each server, and the repair of the mirror from the Src.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceRecoveryTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task A_src_flush_raises_src_records_missing_rebuilds_the_record_at_once_and_restores_the_claim_from_ct()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        await kit.RawSrc.KeyDeleteAsync(kit.SrcRecord);
        await kit.RawSrc.KeyDeleteAsync(rk.SrcClaimKey);
        (await rk.Upkeep.CheckAsync(kit.Group, moved, TestContext.Current.CancellationToken)).Should().Be((UpkeepVerdict.Rebuild, true));

        await rk.Upkeep.CheckOnceAsync(kit.Group, TestContext.Current.CancellationToken);

        rk.Reports.Should().Contain(("src_records_missing", kit.Group));
        var rebuilt = (await kit.ReadStateAsync())!;
        rebuilt.Ep.Should().Be(moved.Ep + 1);
        rebuilt.State.Should().Be("S1");
        rebuilt.Durable.Should().BeTrue();
        (await kit.RecordAsync(RecordSide.Src)).Should().Contain("ep", rebuilt.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.Contain("dur", "1");
        (await rk.SrcFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|", "OPEN with restore from ct");
        (await rk.MirrorFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|");
    }

    [Fact]
    public async Task A_claim_that_lost_its_open_is_restored_from_ct_and_a_healthy_group_is_left_alone()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        await rk.Upkeep.CheckOnceAsync(kit.Group, TestContext.Current.CancellationToken);
        (await kit.ReadStateAsync())!.Ep.Should().Be(moved.Ep, "nothing to repair");

        await kit.RawSrc.HashDeleteAsync(rk.SrcClaimKey, "im:" + kit.Group);
        await rk.Upkeep.CheckOnceAsync(kit.Group, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|");
        (await kit.ReadStateAsync())!.Ep.Should().Be(moved.Ep);
    }

    [Fact]
    public async Task A_dst_flush_is_reasserted_from_the_src_records_with_the_claim_fields_of_the_src_and_the_mirror_rewritten()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        await rk.FlushStateAsync();

        var result = await rk.StateRecovery.ReassertAsync(kit.Group, "confirm", null, null, null, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.ReadStateAsync())!;
        state.Ep.Should().BeGreaterThan(moved.Ep);
        state.State.Should().Be("S1");
        state.Claim.Should().Be(InstanceRecoveryKit.Token, "ct comes from the Src claim");
        state.Move.Should().Be(InstanceMoveClaim.Descriptor);
        (await kit.RecordAsync(RecordSide.Dst))["ep"].Should().Be(state.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await rk.MirrorFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|");
    }

    [Theory]
    [InlineData("Src")]
    [InlineData("Dst")]
    public async Task A_cut_in_the_middle_of_the_commit_on_either_server_is_reasserted_from_the_records_that_survived(string side)
    {
        var cutSide = Enum.Parse<RecordSide>(side);
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None);
        await rk.Steps.OpenAsync(kit.Group, InstanceRecoveryKit.Token, false, null, CancellationToken.None);
        kit.Cuts.Add(new TwoServerKit.Cut(cutSide, "CTLCOMMIT", TwoServerKit.CutMode.Down));
        var stuck = await kit.RunAsync(seeded, TwoServerKit.Target(seeded) with { Claim = InstanceRecoveryKit.Token, Move = InstanceMoveClaim.Descriptor });
        stuck.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        var cutEp = (await kit.ReadStateAsync())!.Ep;
        await rk.FlushStateAsync();

        var result = await rk.StateRecovery.ReassertAsync(kit.Group, "confirm", null, null, null, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        var state = (await kit.ReadStateAsync())!;

        // A Src cut leaves no committed record, so the state is the one the transition started from; a Dst cut still let the Src record commit, so the committed target stands.
        state.State.Should().Be(cutSide == RecordSide.Src ? "S0" : "S1");
        state.Ep.Should().BeGreaterThan(cutEp);
    }

    [Fact]
    public async Task Reassert_takes_the_highest_ep_across_both_servers_and_the_new_ep_is_above_both()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        var higher = moved.Ep + 40;
        await kit.RawSrc.HashSetAsync(kit.SrcRecord, "ep", higher);
        await rk.FlushStateAsync();
        await kit.RawDst.HashSetAsync(kit.DstRecord, [new HashEntry("ep", moved.Ep), new HashEntry("id", moved.Id), new HashEntry("st", "S1"), new HashEntry("dir", "fwd"), new HashEntry("dur", "1"), new HashEntry("np", "0"), new HashEntry("c:c1", "n")]);

        var result = await rk.StateRecovery.ReassertAsync(kit.Group, "confirm", null, null, null, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        (await kit.ReadStateAsync())!.Ep.Should().Be(higher + 1);
    }

    [Fact]
    public async Task The_mirror_is_repaired_from_the_src_field_and_a_mirror_that_is_ahead_is_never_copied_to_the_src()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        await kit.RawDst.KeyDeleteAsync(rk.DstClaimKey);

        await rk.Upkeep.CheckOnceAsync(kit.Group, TestContext.Current.CancellationToken);

        (await rk.MirrorFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|", "OPEN restore copies the Src field");

        var ahead = $"closed|{InstanceRecoveryKit.Token}|inst|{moved.Ep}:S5";
        await kit.RawDst.HashSetAsync(rk.DstClaimKey, "imd:" + kit.Group, ahead);

        await rk.Upkeep.CheckOnceAsync(kit.Group, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().Be($"open|{InstanceRecoveryKit.Token}|inst|", "a mirror cert is evidence, never copied to the Src");
        (await rk.MirrorFieldAsync()).Should().Be(ahead);
        rk.Alerts.Should().Contain(a => a.Code == "claim_mirror_ahead");
    }

    [Fact]
    public async Task An_s0_certificate_is_honoured_only_with_a_durable_state_of_the_same_identity_and_the_matching_rel()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull();
        await rk.SrcClaim.CertifyAsync("im:" + kit.Group, InstanceRecoveryKit.Token, moved.Ep, swept: false);
        await rk.DstClaim.CertifyAsync("imd:" + kit.Group, InstanceRecoveryKit.Token, moved.Ep, swept: false);

        // The certificate is written before DURABLE: the state still shows S1, so the claim is not released.
        await rk.Recovery.RefreshAsync(kit.Group, moved, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("open|");
        rk.Alerts.Should().Contain(a => a.Code == "claim_cert_unpublished");

        // A durable S0 of another identity, or with no rel, does not publish it either.
        var published = moved with { State = "S0", Direction = "none", Durable = true, Release = $"{InstanceRecoveryKit.Token}:{moved.Ep}:S0" };
        await rk.Recovery.RefreshAsync(kit.Group, published with { Ep = moved.Ep + 1 }, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, published with { Release = string.Empty }, TestContext.Current.CancellationToken);
        (await rk.SrcFieldAsync()).Should().StartWith("open|");

        await rk.Recovery.RefreshAsync(kit.Group, published, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("idle|");
        (await rk.MirrorFieldAsync()).Should().StartWith("idle|");
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task An_open_field_with_no_certificate_is_certified_from_a_state_at_s0_and_an_orphan_with_no_state_is_reported()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None);
        await rk.Steps.OpenAsync(kit.Group, InstanceRecoveryKit.Token, false, null, CancellationToken.None);

        await rk.Recovery.RefreshAsync(kit.Group, null, TestContext.Current.CancellationToken);
        rk.Alerts.Should().Contain(a => a.Code == "claim_orphan");
        (await rk.SrcFieldAsync()).Should().StartWith("open|");

        await rk.Recovery.RefreshAsync(kit.Group, seeded with { Claim = InstanceRecoveryKit.Token }, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("idle|").And.Contain($":S0");
        (await rk.MirrorFieldAsync()).Should().StartWith("idle|");
    }

    [Fact]
    public async Task A_pend_the_state_does_not_show_is_released_only_after_the_pend_hold()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None);

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.SrcFieldAsync()).Should().StartWith("pend|");

        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromSeconds(1));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("idle|");
    }

    [Fact]
    public async Task A_pend_the_state_shows_is_left_to_the_resuming_transition()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None);

        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromMinutes(5));
        await rk.Recovery.RefreshAsync(kit.Group, seeded with { Claim = InstanceRecoveryKit.Token }, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("pend|");
    }

    [Fact]
    public async Task A_pend_that_the_stored_acquisition_owns_is_not_released_by_the_pend_hold_and_one_it_does_not_own_is()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.Steps.PendAsync(kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None);
        (await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await rk.Tokens.MarkSrcUncertainAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().BeTrue("the mark is set during every live acquisition");

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromMinutes(5));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("pend|", "the driver reuses the stored token, so recovery leaves its pend alone");
        (await rk.Tokens.ReadAsync(kit.Group, TestContext.Current.CancellationToken)).Should().Be(new AcquisitionToken(InstanceRecoveryKit.Token, true), "recovery never clears the token or the mark while the Src shows a live pend");

        await rk.Tokens.ClearAsync(kit.Group, null, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromSeconds(1));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.SrcFieldAsync()).Should().StartWith("idle|", "with no acquisition behind it the pend is released after the hold");
    }

    [Fact]
    public async Task A_mirror_pend_that_the_stored_acquisition_owns_is_never_released_by_recovery_whatever_ptu_says_or_the_hold_or_the_src_read()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.DstClaim.PendAsync("imd:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);
        await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken);

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.MirrorFieldAsync()).Should().StartWith("pend|", "between the mirror PEND and the mark of the Src PEND");

        await rk.Tokens.MarkSrcUncertainAsync(kit.Group, null, TestContext.Current.CancellationToken);
        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromMinutes(5));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        await rk.SrcClaim.PendAsync("im:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        await kit.RawSrc.KeyDeleteAsync(rk.SrcClaimKey);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromMinutes(5));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.MirrorFieldAsync()).Should().StartWith("pend|", "the hold cancels nothing: only the acquisition or an abandon settles its own mirror");
        (await rk.Tokens.ReadAsync(kit.Group, TestContext.Current.CancellationToken)).Should().Be(new AcquisitionToken(InstanceRecoveryKit.Token, true));
    }

    [Fact]
    public async Task A_settled_claim_of_an_earlier_token_never_deletes_the_baselines_of_a_newer_acquisition()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        const string Older = "fedcba9876543210fedcba9876543210";
        var generation = (await kit.Generations.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken))!.Value;
        await rk.SeedBaselineAsync("dst", new AdmissionBaseline(generation, InstanceRecoveryKit.Token));
        await rk.SeedBaselineAsync("src", new AdmissionBaseline(generation, InstanceRecoveryKit.Token));
        await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken);
        await kit.RawSrc.HashSetAsync(rk.SrcClaimKey, "im:" + kit.Group, $"idle|{Older}|inst|5:S0");

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Token.Should().Be(InstanceRecoveryKit.Token, "the baseline is of another token than the settled claim");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Token.Should().Be(InstanceRecoveryKit.Token);

        // The baseline of the settled token itself goes, unless pt already names a newer acquisition.
        await rk.SeedBaselineAsync("dst", new AdmissionBaseline(generation, Older));
        await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!.Token.Should().Be(Older, "a newer acquisition holds pt");

        await rk.Tokens.ClearAsync(kit.Group, null, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_src_pend_that_lands_after_the_recovery_read_within_the_hold_releases_nothing()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.DstClaim.PendAsync("imd:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);
        await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken);
        await rk.Tokens.MarkSrcUncertainAsync(kit.Group, null, TestContext.Current.CancellationToken);

        // The read shows no Src pend, but the pend whose reply was lost has not landed yet.
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.MirrorFieldAsync()).Should().StartWith("pend|", "one read showing no pend is not enough");
        (await rk.Tokens.ReadAsync(kit.Group, TestContext.Current.CancellationToken)).Should().NotBeNull();

        await rk.SrcClaim.PendAsync("im:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);
        rk.Time.Advance(kit.Options.ClaimPendHold / 2);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.MirrorFieldAsync()).Should().StartWith("pend|");
        (await rk.SrcFieldAsync()).Should().StartWith("pend|");
        (await rk.Tokens.ReadAsync(kit.Group, TestContext.Current.CancellationToken)).Should().Be(new AcquisitionToken(InstanceRecoveryKit.Token, true));
    }

    [Fact]
    public async Task A_mirror_pend_with_no_acquisition_and_no_src_field_is_released_only_after_the_pend_hold()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await rk.DstClaim.PendAsync("imd:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.MirrorFieldAsync()).Should().StartWith("pend|");

        rk.Time.Advance(kit.Options.ClaimPendHold + TimeSpan.FromSeconds(1));
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.MirrorFieldAsync()).Should().StartWith("idle|");
    }

    [Fact]
    public async Task The_decorator_forwards_the_acquisition_token_and_the_commit_guard_to_the_instance_claim()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.SeedAsync();

        var first = await rk.Steps.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken);
        var again = await rk.Steps.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken);

        first.Should().NotBeNull();
        again.Should().Be(first, "the stored token is reused, which only the instance claim does");
        (await rk.Tokens.ReadAsync(kit.Group, TestContext.Current.CancellationToken))!.Token.Should().Be(first);

        var guard = await rk.Steps.CommitGuardAsync(kit.Group, first, null, TestContext.Current.CancellationToken);

        guard.Should().NotBeNull("an unproven marker is stopped by the instance claim, not passed by the default");
        guard.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
    }

    [Fact]
    public async Task An_open_mirror_that_survived_a_dst_promotion_is_confirmed_again_and_the_baseline_follows_only_that_confirmation()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var moved = await rk.MoveToS1Async();
        var gate = new DstAdmissionGate(kit.Generations, rk.Baselines);
        var first = (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken))!;
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();

        // A promotion that kept the OPEN mirror and the control records: the status matches, the baseline is of the old generation.
        kit.Generations.Change(RecordSide.Dst);
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();

        // The confirmation fails (the generation never holds still while the mirror is written): the baseline stays and the gate keeps refusing.
        var unstable = 0;
        kit.Generations.Rewrite = (side, _, actual) => side == RecordSide.Dst && actual is { } g ? g with { ReplicationId = "unstable" + Interlocked.Increment(ref unstable) } : actual;
        await rk.Recovery.RefreshAsync(kit.Group, moved, TestContext.Current.CancellationToken);
        kit.Generations.Rewrite = null;

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(first, "an unconfirmed revalidation never moves the baseline");
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();

        await rk.Recovery.RefreshAsync(kit.Group, moved, TestContext.Current.CancellationToken);

        var now = (await kit.Generations.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken))!.Value;
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().Be(new AdmissionBaseline(now, InstanceRecoveryKit.Token));
        (await gate.CheckAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue("the confirmed write in the new generation moved the baseline");
        (await rk.MirrorFieldAsync()).Should().StartWith("open|");
    }

    [Fact]
    public async Task A_baseline_whose_token_pt_names_is_never_removed_and_one_that_pt_does_not_name_is_an_orphan()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await SeedOrphanAsync(rk);
        await rk.Tokens.WriteAsync(kit.Group, InstanceRecoveryKit.Token, null, TestContext.Current.CancellationToken);

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull("pt names the token");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull();

        await rk.Tokens.WriteAsync(kit.Group, "fedcba9876543210fedcba9876543210", null, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull("pt names another live acquisition, which owns the baselines");

        await rk.Tokens.ClearAsync(kit.Group, null, TestContext.Current.CancellationToken);
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);
        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("src")]
    [InlineData("dst")]
    public async Task A_baseline_whose_token_holds_a_live_claim_on_either_server_is_never_removed(string side)
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await SeedOrphanAsync(rk);
        _ = side == "src"
            ? await rk.SrcClaim.PendAsync("im:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty)
            : await rk.DstClaim.PendAsync("imd:" + kit.Group, InstanceRecoveryKit.Token, InstanceMoveClaim.Descriptor, string.Empty);

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_recovery_that_has_lost_the_lease_removes_no_orphan_baseline_and_one_that_holds_it_does()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var seeded = await kit.SeedAsync();
        await SeedOrphanAsync(rk);
        var lead = kit.Placement.Names(RecordSide.Dst).Lead(kit.Group);
        rk.Fence = _ => new LeaseFence(lead, "holder");

        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull("the lease is not the caller's");
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().NotBeNull();

        await kit.RawDst.StringSetAsync(lead, "holder");
        await rk.Recovery.RefreshAsync(kit.Group, seeded, TestContext.Current.CancellationToken);

        (await rk.Baselines.ReadDstAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
        (await rk.Baselines.ReadSrcAsync(TwoServerKit.App, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static async Task SeedOrphanAsync(InstanceRecoveryKit rk)
    {
        var generation = (await rk.Kit.Generations.ReadAsync(RecordSide.Dst, TestContext.Current.CancellationToken))!.Value;
        await rk.SeedBaselineAsync("dst", new AdmissionBaseline(generation, InstanceRecoveryKit.Token));
        await rk.SeedBaselineAsync("src", new AdmissionBaseline(generation, InstanceRecoveryKit.Token));
    }
}
