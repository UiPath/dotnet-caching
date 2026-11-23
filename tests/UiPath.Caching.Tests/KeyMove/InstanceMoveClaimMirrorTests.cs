using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The mirror PEND is confirmed before the Src CLAIMPEND, and the mirror OPEN is confirmed, in a generation that held, before the commit.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceMoveClaimMirrorTests(TwoServerFixture fixture)
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_mirror_pend_that_cannot_be_confirmed_starts_no_src_claimpend_and_raises_pend_wait_unavailable()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down));

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().ContainSingle(a => a.Code == "pend_wait_unavailable");
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty("the Src CLAIMPEND does not start without a confirmed mirror pend");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Absent);
    }

    [Fact]
    public async Task A_mirror_pend_whose_wait_never_confirms_starts_no_src_claimpend()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var alerts = new List<(string Group, string Code, string Detail)>();

        // A connection without allowAdmin cannot prove its WAIT, so the mirror pend is written and never confirmed.
        await using var plain = await ConnectionMultiplexer.ConnectAsync(TwoServerFixture.Dst!);
        var unproven = new ClaimStore(plain.GetDatabase(), kit.Placement.Claim(RecordSide.Dst).Key, true, kit.DstCaps.Version) { BarrierRetries = 1 };
        var claim = new InstanceMoveClaim(kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps), unproven, kit.Generations, _ => true, (g, c, d) => alerts.Add((g, c, d)));

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().ContainSingle(a => a.Code == "pend_wait_unavailable");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|", "written, not confirmed");
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_mirror_pend_whose_reply_was_lost_starts_no_src_step_and_the_retry_confirms_and_completes()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Lost, times: 1);
        kit.Cuts.Add(cut);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);
        cut.Fired.Should().Be(1);
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty();

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        alerts.Should().ContainSingle(a => a.Code == "pend_wait_unavailable");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Pending);
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|");
    }

    [Fact]
    public async Task An_unconfirmed_mirror_pend_that_the_dst_loses_is_written_again_by_the_retry_before_the_src_pend()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Lost, times: 1);
        kit.Cuts.Add(cut);
        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        // The Dst fails over to a replica that never had the write: the mirror field is gone.
        await kit.RawDst.HashDeleteAsync(key, "imd:" + kit.Group);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be($"pend|{Token}|inst|");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Pending);
        alerts.Should().ContainSingle(a => a.Code == "pend_wait_unavailable");
    }

    [Fact]
    public async Task A_src_pend_that_is_refused_releases_the_mirror_pend_so_the_next_token_is_not_refused_by_it()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        var src = kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps);
        (await src.PendAsync("im:other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "inst", string.Empty)).Should().Be(ClaimCodes.Done);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
    }

    [Fact]
    public async Task A_src_pend_whose_reply_was_lost_is_read_back_and_counts_as_pended_with_the_mirror_kept()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Lost, times: 1);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done, "the Src shows the pend under the token");

        kit.SrcPendCut.Fired.Should().Be(1);
        alerts.Should().BeEmpty();
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().Be($"pend|{Token}|inst|");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Pending);
    }

    [Fact]
    public async Task A_src_pend_whose_reply_was_lost_and_that_the_read_back_does_not_show_keeps_the_mirror_pend_and_raises_claim_pend_uncertain()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var srcKey = kit.Placement.Claim(RecordSide.Src).Key;
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Lost, times: 1);

        // The pend applied and is gone again when the Src is read: the command may still land, so the mirror stays for this attempt.
        kit.BeforeSrcHashGetAll = () => kit.RawSrc.HashDelete(srcKey, "im:" + kit.Group);
        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        kit.SrcPendCut.Fired.Should().Be(1);
        alerts.Select(a => a.Code).Should().Equal("claim_pend_uncertain");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().Be($"pend|{Token}|inst|", "the mirror stays");
        kit.BeforeSrcHashGetAll = null;
        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done, "the retry with the same token finds both pends");
    }

    [Fact]
    public async Task A_src_pend_that_was_never_sent_releases_the_mirror_pend()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Down, times: 1);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().BeEmpty();
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_confirmed_mirror_open_is_recorded_and_a_second_open_in_the_same_generation_raises_nothing()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mirror_open_that_cannot_be_confirmed_holds_the_commit_even_past_the_marker()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var seeded = await kit.SeedAsync();
        var token = Token;
        await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken);
        var options = new SequenceOptions(BeforeCommit: Hook(claim, kit, token));
        var srcCommit = new TwoServerKit.Cut(RecordSide.Src, "CTLCOMMIT", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(srcCommit);
        var stopped = await kit.RunAsync(seeded, TwoServerKit.Target(seeded), options);
        stopped.Outcome.Should().Be(TransitionOutcome.CommitIncomplete, "the marker is written and a record write did not take");
        (await kit.ReadStateAsync())!.Field(StateFields.CommitBegun).Should().Be((await kit.ReadStateAsync())!.Id);
        srcCommit.Lift();
        var mirror = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(mirror);
        var commitsBefore = kit.Trace.Count(t => t == "dst:ctlcommit");

        var blocked = await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken);

        blocked.Outcome.Should().Be(TransitionOutcome.BarrierStuck, "past the marker the mirror OPEN is the one step that is never skipped");
        kit.Trace.Count(t => t == "dst:ctlcommit").Should().Be(commitsBefore, "no commit step ran while the mirror was open-unconfirmed");
        (await kit.ReadStateAsync())!.Durable.Should().BeFalse();
        alerts.Should().Contain(a => a.Code == "mirror_open_unavailable");

        mirror.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_dst_generation_change_after_open_holds_the_commit_until_a_confirmed_repair()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        alerts.Should().BeEmpty();

        // The Dst fails over to a replica that lost the mirror, and the repair cannot be confirmed yet.
        kit.Generations.Change(RecordSide.Dst);
        await kit.RawDst.HashDeleteAsync(key, "imd:" + kit.Group);
        var down = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(down);

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed, "having seen the new generation is not a repair");

        alerts.Select(a => a.Code).Should().Equal("mirror_repair_pending", "mirror_repair_pending");
        down.Lift();
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|");
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        alerts.Should().HaveCount(2, "the repaired generation is the new baseline");
    }

    [Fact]
    public async Task A_dst_generation_that_changes_while_the_mirror_is_opened_is_not_recorded_and_blocks()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Generations.Rewrite = (side, count, real) => side == RecordSide.Dst && count == 2 && real is { } g ? g with { RunId = g.RunId + "#moved" } : real;

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);

        alerts.Should().ContainSingle(a => a.Code == "mirror_open_unavailable");
        kit.Generations.Rewrite = null;
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mirror_that_the_dst_does_not_show_open_under_this_token_after_the_confirmed_write_is_not_recorded_and_blocks(bool otherToken)
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.BeforeDstHashGetAll = () =>
        {
            if (otherToken)
            {
                kit.RawDst.HashSet(key, "imd:" + kit.Group, "open|fedcba9876543210fedcba9876543210|inst|");
            }
            else
            {
                kit.RawDst.HashDelete(key, "imd:" + kit.Group);
            }
        };

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);

        alerts.Should().ContainSingle(a => a.Code == "mirror_open_unavailable");
        kit.BeforeDstHashGetAll = null;
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(otherToken ? ClaimOutcome.DoneUnconfirmed : ClaimOutcome.Done, "the next open writes the mirror again, and another token's open on the Dst is a conflict");
    }

    [Fact]
    public async Task An_unknown_dst_generation_is_never_unchanged_and_holds_the_commit_with_the_alert_of_its_state()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Generations.Unknown(RecordSide.Dst, true);

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);
        kit.Generations.Unknown(RecordSide.Dst, false);
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        kit.Generations.Unknown(RecordSide.Dst, true);
        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);

        alerts.Select(a => a.Code).Should().Equal("mirror_open_unavailable", "mirror_repair_pending");
    }

    [Fact]
    public async Task A_dst_generation_change_holds_the_commit_of_a_sequence_until_the_mirror_is_repaired()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var seeded = await kit.SeedAsync();
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var options = new SequenceOptions(BeforeCommit: Hook(claim, kit, Token));
        var cmt = new TwoServerKit.Cut(RecordSide.Dst, "CMT", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cmt);
        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded), options)).Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        var cmtBefore = kit.Trace.Count(t => t == "dst:cmt");
        kit.Generations.Change(RecordSide.Dst);
        var down = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(down);
        cmt.Lift();

        (await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.BarrierStuck);

        kit.Trace.Count(t => t == "dst:cmt").Should().Be(cmtBefore, "CMT waits for the repair");
        alerts.Should().Contain(a => a.Code == "mirror_repair_pending");
        down.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Count(t => t == "dst:cmt").Should().Be(cmtBefore + 1);
    }

    [Fact]
    public async Task A_retry_by_a_fresh_run_after_a_lost_mirror_pend_reply_reuses_the_stored_token_and_is_not_refused_by_the_surviving_pend()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (first, _) = Claim(kit, tokens: true);
        var token = (await first.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendToken)).ToString().Should().Be(token, "the token is stored before anything is pended");
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Lost, times: 1));
        (await first.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        // Another run of the driver, with nothing in memory.
        var (second, alerts) = Claim(kit, tokens: true);
        var again = await second.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken);

        again.Should().Be(token);
        (await second.PendAsync(kit.Group, again, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        alerts.Should().BeEmpty();
        (await second.StatusAsync(kit.Group, token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Pending);
    }

    [Fact]
    public async Task A_token_whose_mirror_pend_was_released_is_dropped_so_the_next_acquisition_takes_a_new_one()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (claim, _) = Claim(kit, tokens: true);
        var src = kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps);
        (await src.PendAsync("im:other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "inst", string.Empty)).Should().Be(ClaimCodes.Done);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeFalse("the released mirror pend is spent with its token");
        (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().NotBe(token);
    }

    [Fact]
    public async Task A_release_that_cannot_be_applied_keeps_the_stored_token_until_a_retry_settles_the_mirror()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (claim, _) = Claim(kit, tokens: true);
        var src = kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps);
        (await src.PendAsync("im:other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "inst", string.Empty)).Should().Be(ClaimCodes.Done);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        var down = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down, skip: 1);
        kit.Cuts.Add(down);

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendToken)).ToString().Should().Be(token, "the mirror still holds the pend of this token");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|");

        down.Lift();
        (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().Be(token);
        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);
        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeFalse("the release is applied now");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
    }

    [Fact]
    public async Task A_release_whose_reply_was_lost_after_it_applied_is_settled_by_the_read_back_and_the_token_is_dropped()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (claim, _) = Claim(kit, tokens: true);
        var src = kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps);
        (await src.PendAsync("im:other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "inst", string.Empty)).Should().Be(ClaimCodes.Done);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Lost, times: 1, skip: 1));

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeFalse("the Dst shows the mirror idle under the token");
    }

    [Fact]
    public async Task A_crash_after_the_src_pend_applied_leaves_the_mirror_in_place_for_a_fresh_run_whose_attempt_is_never_sent()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (first, _) = Claim(kit, tokens: true);
        var token = (await first.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        await CrashAfterSrcPendAsync(kit, token);

        var (second, alerts) = Claim(kit, tokens: true);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Down, times: 1);
        (await second.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().Be(token);
        var outcome = await second.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken);

        outcome.Should().Be(ClaimOutcome.Done, "the read-back shows the pend of the attempt that crashed");
        kit.SrcPendCut.Fired.Should().Be(1, "this attempt's own command was never sent");
        alerts.Should().BeEmpty();
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|", "the mirror stays");
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendToken)).ToString().Should().Be(token);
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendUncertain)).ToString().Should().Be("1");
    }

    [Fact]
    public async Task A_mark_that_cannot_be_stored_sends_no_src_pend_and_leaves_the_mirror_pend_for_the_next_attempt()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var failing = new MarkTokens(kit.Tokens) { Fails = true };
        var (claim, alerts) = Claim(kit, custom: failing);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().ContainSingle(a => a.Code == "pend_wait_unavailable");
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty("no Src pend is sent without a stored mark");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|", "the mirror pend stays");
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendToken)).ToString().Should().Be(token);

        failing.Fails = false;
        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done, "the next attempt handles the mirror pend with the same token");
    }

    [Fact]
    public async Task The_mark_is_stored_before_the_src_pend_exists()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var marks = new MarkTokens(kit.Tokens);
        marks.OnMark = async () => marks.SrcPendExistedAtMark = await kit.RawSrc.HashExistsAsync(kit.Placement.Claim(RecordSide.Src).Key, "im:" + kit.Group);
        var (claim, _) = Claim(kit, custom: marks);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        marks.Marks.Should().Be(1);
        marks.SrcPendExistedAtMark.Should().BeFalse();
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendUncertain)).ToString().Should().Be("1", "the mark stays after a pend that succeeded, until TRANSIT clears it");
    }

    [Fact]
    public async Task A_pend_that_succeeded_before_a_failed_transit_is_met_again_by_a_retry_that_never_sends_its_own_and_nothing_is_released()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (first, _) = Claim(kit, tokens: true);
        var token = (await first.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        (await first.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        // The TRANSIT that would bind the claim fails before it applies: the state keeps the token and the mark.
        var (second, _) = Claim(kit, tokens: true);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Down, times: 1);
        (await second.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().Be(token);
        (await second.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        kit.SrcPendCut.Fired.Should().Be(1);
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|");
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendToken)).ToString().Should().Be(token);
        (await kit.RawDst.HashGetAsync(kit.StateKey, StateFields.PendUncertain)).ToString().Should().Be("1");
    }

    [Fact]
    public async Task A_src_read_back_that_shows_no_pend_lets_a_later_attempt_release_the_mirror_and_drop_token_and_mark()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (first, _) = Claim(kit, tokens: true);
        var token = (await first.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        await CrashAfterSrcPendAsync(kit, token);

        // The Src fails over to a replica that never had the unconfirmed pend.
        await kit.RawSrc.HashDeleteAsync(kit.Placement.Claim(RecordSide.Src).Key, "im:" + kit.Group);
        var (second, _) = Claim(kit, tokens: true);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Down, times: 1);
        var outcome = await second.PendAsync(kit.Group, (await second.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!, "inst", null, TestContext.Current.CancellationToken);

        outcome.Should().Be(ClaimOutcome.Failed);
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeFalse();
        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendUncertain)).Should().BeFalse("the mark is dropped with the token");
    }

    [Fact]
    public async Task A_src_that_cannot_be_read_back_keeps_the_mirror_and_raises_claim_pend_uncertain()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (first, _) = Claim(kit, tokens: true);
        var token = (await first.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        await CrashAfterSrcPendAsync(kit, token);
        await kit.RawSrc.HashDeleteAsync(kit.Placement.Claim(RecordSide.Src).Key, "im:" + kit.Group);
        var (second, alerts) = Claim(kit, tokens: true);
        kit.SrcPendCut = new TwoServerKit.Cut(RecordSide.Src, "pend", TwoServerKit.CutMode.Down, times: 1);
        kit.BeforeSrcHashGetAll = () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "the read failed", null, CommandStatus.Sent);

        (await second.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().ContainSingle(a => a.Code == "claim_pend_uncertain");
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|");
        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeTrue();
    }

    [Fact]
    public async Task A_stored_token_that_the_mirror_refuses_as_spent_is_dropped()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        await kit.SeedAsync();
        var (claim, _) = Claim(kit, tokens: true);
        var token = (await claim.AcquireTokenAsync(kit.Group, null, TestContext.Current.CancellationToken))!;
        await kit.RawDst.HashSetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group, $"idle|{token}|inst|");

        (await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        (await kit.RawDst.HashExistsAsync(kit.StateKey, StateFields.PendToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_dst_failover_between_the_open_and_the_marker_that_loses_the_mirror_stops_the_commit_until_a_confirmed_repair()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var seeded = await kit.SeedAsync();
        var mirrorKey = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var failOver = true;
        var options = new SequenceOptions(
            BeforeCommit: async ct =>
            {
                if (await claim.OpenAsync(kit.Group, Token, restore: false, null, ct) != ClaimOutcome.Done)
                {
                    return new TransitionResult(TransitionOutcome.BarrierStuck);
                }

                if (failOver)
                {
                    // The Dst fails over to a replica that kept the state and the lease but never had the mirror OPEN.
                    failOver = false;
                    kit.Generations.Change(RecordSide.Dst);
                    await kit.RawDst.HashDeleteAsync(mirrorKey, "imd:" + kit.Group);
                }

                return null;
            },
            AfterMarker: (proof, ct) => claim.CommitGuardAsync(kit.Group, Token, proof is { } p ? kit.Generations.Shifted(RecordSide.Dst, p) : null, ct));

        var stopped = await kit.RunAsync(seeded, TwoServerKit.Target(seeded), options);

        stopped.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        alerts.Should().ContainSingle(a => a.Code == "mirror_repair_pending");
        kit.Trace.Should().NotContain("dst:ctlcommit", "no commit step runs after a marker written in a generation the mirror was not confirmed in");
        (await kit.RawDst.HashExistsAsync(mirrorKey, "imd:" + kit.Group)).Should().BeFalse();
        (await kit.ReadStateAsync())!.Durable.Should().BeFalse();

        (await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);

        (await kit.RawDst.HashGetAsync(mirrorKey, "imd:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|", "the repair is a confirmed write");
        alerts.Should().ContainSingle(a => a.Code == "mirror_repair_pending", "the repair raised nothing further");
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    // The commit hook as the driver builds it: the claim open, and a result of anything but done is a resumable stop.
    private static Func<CancellationToken, Task<TransitionResult?>> Hook(InstanceMoveClaim claim, TwoServerKit kit, string token) =>
        async ct => await claim.OpenAsync(kit.Group, token, restore: false, null, ct) switch
        {
            ClaimOutcome.Done => null,
            ClaimOutcome.Refused => new TransitionResult(TransitionOutcome.StateChanged, Detail: "claim refused"),
            _ => new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the move claim open is not confirmed"),
        };

    // What a run that died right after its Src pend applied leaves behind: the stored mark, the confirmed mirror pend and the Src pend, and nothing after.
    private static async Task CrashAfterSrcPendAsync(TwoServerKit kit, string token)
    {
        (await kit.Tokens.MarkSrcUncertainAsync(kit.Group, null, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps).PendConfirmedAsync("imd:" + kit.Group, token, "inst")).Code.Should().Be(ClaimCodes.Done);
        (await kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps).PendAsync("im:" + kit.Group, token, "inst", string.Empty)).Should().Be(ClaimCodes.Done);
    }

    private static (InstanceMoveClaim Claim, List<(string Group, string Code, string Detail)> Alerts) Claim(TwoServerKit kit, bool tokens = false, IAcquisitionTokens? custom = null)
    {
        var alerts = new List<(string Group, string Code, string Detail)>();
        var claim = new InstanceMoveClaim(
            kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps),
            kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps),
            kit.Generations,
            _ => true,
            (g, c, d) => alerts.Add((g, c, d)),
            custom ?? (tokens ? kit.Tokens : null));
        return (claim, alerts);
    }

    // The kit's token store with the mark under the test's control.
    private sealed class MarkTokens(IAcquisitionTokens inner) : IAcquisitionTokens
    {
        public bool Fails { get; set; }

        public int Marks { get; private set; }

        public bool SrcPendExistedAtMark { get; set; }

        public Func<Task>? OnMark { get; set; }

        public Task<AcquisitionToken?> ReadAsync(string group, CancellationToken cancellationToken) => inner.ReadAsync(group, cancellationToken);

        public Task<bool> WriteAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => inner.WriteAsync(group, token, fence, cancellationToken);

        public async Task<bool> MarkSrcUncertainAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
        {
            Marks++;
            if (OnMark is { } hook)
            {
                await hook();
            }

            return !Fails && await inner.MarkSrcUncertainAsync(group, fence, cancellationToken);
        }

        public Task<bool> ClearAsync(string group, LeaseFence? fence, CancellationToken cancellationToken) => inner.ClearAsync(group, fence, cancellationToken);
    }
}
