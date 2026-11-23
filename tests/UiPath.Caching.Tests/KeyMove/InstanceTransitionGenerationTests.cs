using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The final verification records the records of both servers and both generations, and the generations are rechecked as a step of their own just before DURABLE.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceTransitionGenerationTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task The_final_verification_records_every_record_of_both_servers_and_both_generations()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();

        (await kit.RunAsync(seeded, TwoServerKit.Target(seeded))).Outcome.Should().Be(TransitionOutcome.Completed);

        var final = kit.Sequence.LastFinal!;
        final.Records.Where(r => r.Side == RecordSide.Src).Select(r => r.Key).Should().BeEquivalentTo(kit.Placement.Control(RecordSide.Src, kit.Group).Select(r => r.Key));
        final.Records.Where(r => r.Side == RecordSide.Dst).Select(r => r.Key).Should().BeEquivalentTo(kit.Placement.Control(RecordSide.Dst, kit.Group).Select(r => r.Key));
        final.Records.Should().NotBeEmpty();
        IGenerationSource source = kit.Generations;
        final.Generations.Should().Be((await source.ReadPairAsync(TestContext.Current.CancellationToken))!.Value);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void A_verification_of_one_server_alone_does_not_cover_both(bool src, bool dst, bool expected)
    {
        RedisKey srcKey = "s";
        RedisKey dstKey = "d";
        var records = new List<(RecordSide, RedisKey)>();
        if (src)
        {
            records.Add((RecordSide.Src, srcKey));
        }

        if (dst)
        {
            records.Add((RecordSide.Dst, dstKey));
        }

        var final = new FinalVerification(records, default);

        final.Covers([srcKey], [dstKey]).Should().Be(expected);
    }

    [Fact]
    public void A_verification_does_not_cover_a_server_with_no_expected_record_or_a_record_it_missed()
    {
        RedisKey dstKey = "d";
        var dstOnly = new FinalVerification([(RecordSide.Dst, dstKey)], default);

        dstOnly.Covers([], [dstKey]).Should().BeFalse("both servers are required");
        new FinalVerification([(RecordSide.Src, "s1"), (RecordSide.Dst, dstKey)], default).Covers(["s1", "s2"], [dstKey]).Should().BeFalse("a record of the Src was not verified");
    }

    [Fact]
    public async Task A_generation_that_differs_at_the_recheck_repeats_the_commit_and_verification_and_then_completes()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var failedOver = false;

        // Reads of the Dst: 1 and 2 bracket the first verification, 3 is the recheck, which finds another generation.
        kit.Generations.Rewrite = (side, count, real) =>
        {
            if (side == RecordSide.Dst && count == 3 && real is { } g)
            {
                failedOver = true;
                return g with { RunId = g.RunId + "#failover" };
            }

            return real;
        };

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        failedOver.Should().BeTrue();
        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Count(t => t == "dst:ctlcommit").Should().Be(2, "the commit is sent again");
        kit.Trace.Count(t => t == "src:ctlcommit").Should().Be(2);
        kit.Trace.Count(t => t == "dst:durable").Should().Be(1);
        kit.Trace.IndexOf("dst:durable").Should().BeGreaterThan(kit.Trace.LastIndexOf("src:ctlcommit"), "DURABLE follows the second verification");
        kit.Generations.Reads(RecordSide.Dst).Should().Be(6, "the verification is bracketed and rechecked again");
        kit.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_generation_at_the_recheck_counts_as_a_mismatch()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.Generations.Rewrite = (side, count, real) => side == RecordSide.Src && count == 3 ? null : real;

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Trace.Count(t => t == "src:ctlcommit").Should().Be(2, "an unknown generation sent the sequence back");
        kit.Generations.Reads(RecordSide.Src).Should().Be(6);
    }

    [Fact]
    public async Task A_generation_that_stays_unreadable_ends_resumable_with_one_alert_and_never_writes_durable()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.Generations.Unknown(RecordSide.Dst, true);

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        kit.Reports.Count(r => r.Kind == "barrier_stuck").Should().Be(1, "one alert for the attempt");
        kit.Trace.Should().NotContain("dst:durable");
        (await kit.ReadStateAsync())!.Durable.Should().BeFalse();

        kit.Generations.Unknown(RecordSide.Dst, false);
        (await kit.Sequence.ResumeAsync(kit.Group, null, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ReadStateAsync())!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_generation_that_differs_at_every_recheck_ends_resumable_with_one_alert_and_never_writes_durable()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        kit.Generations.Rewrite = (side, count, real) => side == RecordSide.Dst && count % 3 == 0 && real is { } g ? g with { RunId = g.RunId + "#" + count } : real;

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        kit.Reports.Count(r => r.Kind == "barrier_stuck").Should().Be(1);
        kit.Trace.Should().NotContain("dst:durable");
    }

    [Fact]
    public async Task A_generation_that_moves_during_the_verification_is_not_recorded()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        // The Src read before the verification differs from the one after it, in every round.
        kit.Generations.Rewrite = (side, count, real) => side == RecordSide.Src && count % 3 == 1 && real is { } g ? g with { RunId = g.RunId + "#" + count } : real;

        var result = await kit.RunAsync(seeded, TwoServerKit.Target(seeded));

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        kit.Sequence.LastFinal.Should().BeNull();
        kit.Trace.Should().NotContain("dst:durable");
    }

    [Fact]
    public async Task A_recheck_that_fails_after_the_certificate_was_written_leaves_a_certificate_that_grants_nothing()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var claim = NewClaim(kit);
        await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken);
        var target = TwoServerKit.Target(seeded);
        var failOver = true;
        var options = new SequenceOptions(
            Release: $"{token}:{target.Ep}:S0",
            BeforeCommit: async ct => await claim.OpenAsync(kit.Group, token, restore: false, null, ct) == ClaimOutcome.Done ? null : new TransitionResult(TransitionOutcome.BarrierStuck),
            BeforeDurable: async ct =>
            {
                var certified = await claim.CertifyAsync(kit.Group, token, target.Ep, "S0", null, ct);

                // The Dst fails over after the certificate was written: the recheck that follows finds another generation.
                if (failOver)
                {
                    kit.Generations.Change(RecordSide.Dst);
                }

                return certified == ClaimOutcome.Failed ? new TransitionResult(TransitionOutcome.BarrierStuck) : null;
            });

        var result = await kit.RunAsync(seeded, target, options);

        result.Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        await AssertUnpublishedCertificateAsync(kit, claim, token, target.Ep);

        // The certificate takes effect only with the DURABLE of the same identity.
        failOver = false;
        (await kit.Sequence.ResumeAsync(kit.Group, options, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.ReadStateAsync())!.Release.Should().Be($"{token}:{target.Ep}:S0");
        (await claim.ReleaseIdleAsync(kit.Group, token, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
    }

    [Fact]
    public async Task The_operator_does_not_certify_a_claim_from_a_committed_record_while_the_state_is_not_published()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var claim = NewClaim(kit);
        await claim.PendAsync(kit.Group, token, "inst", null, TestContext.Current.CancellationToken);
        var target = TwoServerKit.Target(seeded);
        var failOver = true;
        var down = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        var added = false;
        var options = new SequenceOptions(
            Release: $"{token}:{target.Ep}:S0",
            BeforeCommit: OpenOnce(kit, claim, token),
            BeforeDurable: async ct =>
            {
                // The Src certificate is written, the mirror's is not (the Dst cannot be written), and the recheck then finds another Dst generation.
                if (!added)
                {
                    added = true;
                    kit.Cuts.Add(down);
                }

                await claim.CertifyAsync(kit.Group, token, target.Ep, "S0", null, ct);
                if (failOver)
                {
                    kit.Generations.Change(RecordSide.Dst);
                }

                return null;
            });
        (await kit.RunAsync(seeded, target, options)).Outcome.Should().Be(TransitionOutcome.CommitIncomplete);
        (await kit.RecordAsync(RecordSide.Src))["dur"].Should().Be("1", "the control record is committed");
        down.Lift();
        var operatorClaim = await AssertOperatorRefusesUnpublishedCertificateAsync(kit, token);

        // The DURABLE of the same identity publishes the state; only then may the operator settle the mirror that stayed certless.
        failOver = false;
        down = new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(down);
        (await kit.Sequence.ResumeAsync(kit.Group, options with { BeforeCommit = null }, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        down.Lift();
        (await operatorClaim.CertifyAsync("imd:" + kit.Group, $"open|{token}|inst|", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);
    }

    // Later commit attempts run with the Dst unavailable.
    private static Func<CancellationToken, Task<TransitionResult?>> OpenOnce(TwoServerKit kit, InstanceMoveClaim claim, string token)
    {
        var opened = false;
        return async ct =>
        {
            if (opened)
            {
                return null;
            }

            opened = true;
            return await claim.OpenAsync(kit.Group, token, restore: false, null, ct) == ClaimOutcome.Done ? null : new TransitionResult(TransitionOutcome.BarrierStuck);
        };
    }

    private static InstanceMoveClaim NewClaim(TwoServerKit kit) =>
        new InstanceMoveClaim(
            kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps),
            kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps),
            kit.Generations,
            _ => true,
            null);

    private static async Task AssertUnpublishedCertificateAsync(TwoServerKit kit, InstanceMoveClaim claim, string token, long ep)
    {
        var state = (await kit.ReadStateAsync())!;
        state.Durable.Should().BeFalse("DURABLE is never written after a failed recheck");
        state.Release.Should().BeEmpty("the release certificate of the state is written by DURABLE only");
        var claimKey = kit.Placement.Claim(RecordSide.Src).Key;
        var src = ClaimGrammar.Parse((await kit.RawSrc.HashGetAsync(claimKey, "im:" + kit.Group)).ToString());
        var dst = ClaimGrammar.Parse((await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString());
        src.Cert.Should().Be($"{ep}:S0", "the certificate was written");
        new[] { src, dst }.Should().OnlyContain(v => v.Status == ClaimGrammar.Open && v.IsLive, "a certificate does not end the claim: admission still sees a live move");
        (await claim.StatusAsync(kit.Group, token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Open);

        // No claim step takes the certificate as release: another group's move is still refused.
        var other = NewClaim(kit);
        (await other.PendAsync(kit.Group + "x", "fedcba9876543210fedcba9876543210", "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);
    }

    private static async Task<ClaimOperator> AssertOperatorRefusesUnpublishedCertificateAsync(TwoServerKit kit, string token)
    {
        var mirrorKey = kit.Placement.Claim(RecordSide.Dst).Key;
        var operatorClaim = new ClaimOperator(kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps), new KitEvidence(kit));
        var shown = $"open|{token}|inst|";

        (await operatorClaim.CertifyAsync("imd:" + kit.Group, shown, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.NoEvidence, "a committed record is not a published state");
        (await kit.RawDst.HashGetAsync(mirrorKey, "imd:" + kit.Group)).ToString().Should().Be(shown);
        return operatorClaim;
    }

    private sealed class KitEvidence(TwoServerKit kit) : IClaimEvidence
    {
        public async Task<ClaimTerminal?> ReadTerminalAsync(string group, CancellationToken cancellationToken)
        {
            var record = await kit.RecordAsync(RecordSide.Src);
            if (!record.TryGetValue("dur", out var durable) || durable != "1")
            {
                return null;
            }

            var state = await kit.ReadStateAsync();
            return new ClaimTerminal(long.Parse(record["ep"], System.Globalization.CultureInfo.InvariantCulture), Swept: false, state is { Durable: true } && state.Id == record["id"] ? state.Release : null);
        }

        public Task<bool> ShowsTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> RemovableAsync(string group, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
