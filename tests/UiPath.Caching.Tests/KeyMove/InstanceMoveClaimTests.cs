using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The instance claim on the Src and its mirror on the Dst.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class InstanceMoveClaimTests(TwoServerFixture fixture)
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(1, "Confirmed", "Done")]
    [InlineData(2, "NoReplica", "Done")]
    [InlineData(1, "NotConfirmed", "DoneUnconfirmed")]
    [InlineData(2, "NotConfirmed", "DoneUnconfirmed")]
    [InlineData(0, "NotApplicable", "Refused")]
    [InlineData(-3, "NotApplicable", "Refused")]
    [InlineData(-4, "NotApplicable", "Refused")]
    [InlineData(-6, "NotApplicable", "Refused")]
    [InlineData(-7, "NotApplicable", "Failed")]
    [InlineData(-8, "NotApplicable", "Failed")]
    [InlineData(-9, "NotApplicable", "Failed")]
    public void The_claim_codes_map_to_outcomes_and_an_unconfirmed_step_is_done_unconfirmed(long code, string durability, string expected) =>
        InstanceMoveClaim.Map(new ClaimResult(code, Enum.Parse<ClaimDurability>(durability))).Should().Be(Enum.Parse<ClaimOutcome>(expected));

    [Fact]
    public async Task Pend_writes_the_mirror_and_the_src_and_open_writes_the_src_then_the_mirror_under_the_prefixed_name()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Src).Key;

        (await claim.PendAsync(kit.Group, Token, "ignored", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be($"pend|{Token}|inst|", "the mirror pend comes with the Src pend");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Pending);

        (await claim.OpenAsync(kit.Group, Token, restore: false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Open);
        (await kit.RawSrc.HashGetAsync(key, "im:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|");
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|");

        // A plain prefixed pod reads the same key: the raw key on the Src server carries the connection prefix, the mirror on the Dst none.
        await using var rawMux = await ConnectionMultiplexer.ConnectAsync($"{TwoServerFixture.Src},allowAdmin=true");
        var raw = rawMux.GetDatabase();
        (await raw.HashGetAsync((RedisKey)(kit.SrcPrefix + (string?)key), "im:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|");
    }

    [Fact]
    public async Task Pend_by_another_token_is_refused_while_the_first_is_live()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);

        (await claim.PendAsync(kit.Group, "fedcba9876543210fedcba9876543210", "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);
    }

    [Fact]
    public async Task Open_is_idempotent_when_the_reply_of_the_first_was_lost()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        var cut = new TwoServerKit.Cut(RecordSide.Src, "claim", TwoServerKit.CutMode.Lost, times: 1);
        kit.Cuts.Add(cut);

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed, "the outcome is unknown, so the step is not taken as done");
        cut.Fired.Should().Be(1);
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Open, "the write applied");

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("open|");
    }

    [Theory]
    [InlineData("imd:other", "inst")]
    [InlineData("rt:other", "rt")]
    public async Task A_dst_record_that_refuses_the_mirror_pend_raises_claim_conflict_and_the_src_claim_is_not_started(string live, string descriptor)
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var dst = kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps);
        (await dst.OpenAsync(live, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", descriptor, restore: true)).Code.Should().Be(1);

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Refused);

        alerts.Should().ContainSingle(a => a.Code == "claim_conflict");
        (await kit.RawDst.HashExistsAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).Should().BeFalse();
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty("the mirror comes first, so a refusal starts no Src step");
    }

    [Fact]
    public async Task A_dst_record_that_refuses_the_mirror_open_raises_claim_conflict_and_holds_the_commit()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        // The mirror pend is lost and another move takes its place on the Dst record.
        await kit.RawDst.HashDeleteAsync(key, "imd:" + kit.Group);
        var dst = kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps);
        (await dst.OpenAsync("imd:other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "inst", restore: true)).Code.Should().Be(1);

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);

        alerts.Should().ContainSingle(a => a.Code == "claim_conflict");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Open, "the Src step applied");
    }

    [Fact]
    public async Task A_mirror_that_lost_its_field_is_repaired_by_the_next_open_and_follows_certificate_and_close()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken);
        await kit.RawDst.HashDeleteAsync(key, "imd:" + kit.Group);

        (await claim.OpenAsync(kit.Group, Token, restore: true, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be($"open|{Token}|inst|");

        (await claim.CertifyAsync(kit.Group, Token, 12, "S5", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        var srcCert = (await kit.RawSrc.HashGetAsync(key, "im:" + kit.Group)).ToString();
        srcCert.Should().StartWith($"open|{Token}|inst|").And.NotEndWith("|");
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().Be(srcCert, "the mirror carries the same certificate");

        (await claim.CloseAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await kit.RawSrc.HashGetAsync(key, "im:" + kit.Group)).ToString().Should().StartWith("closed|");
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().StartWith("closed|");
        alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_abandon_after_open_certifies_and_releases_idle_on_the_src_and_the_mirror_and_a_new_move_may_start()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        var key = kit.Placement.Claim(RecordSide.Dst).Key;
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken);

        (await claim.CertifyAsync(kit.Group, Token, 12, "S0", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
        (await claim.ReleaseIdleAsync(kit.Group, Token, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);

        (await kit.RawSrc.HashGetAsync(key, "im:" + kit.Group)).ToString().Should().StartWith("idle|");
        (await kit.RawDst.HashGetAsync(key, "imd:" + kit.Group)).ToString().Should().StartWith("idle|");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Absent);
        (await claim.PendAsync(kit.Group, "fedcba9876543210fedcba9876543210", "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Done);
    }

    [Fact]
    public async Task A_src_step_that_fails_is_failed_and_the_mirror_is_not_opened()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, _) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Src, "claim", TwoServerKit.CutMode.Down));

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        (await kit.RawDst.HashGetAsync(kit.Placement.Claim(RecordSide.Dst).Key, "imd:" + kit.Group)).ToString().Should().StartWith("pend|");
    }

    [Fact]
    public async Task A_mirror_open_that_cannot_be_written_holds_the_commit_with_mirror_open_unavailable()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var (claim, alerts) = Claim(kit);
        await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken);
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "claim", TwoServerKit.CutMode.Down));

        (await claim.OpenAsync(kit.Group, Token, false, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.DoneUnconfirmed);

        alerts.Should().ContainSingle(a => a.Code == "mirror_open_unavailable");
        (await claim.StatusAsync(kit.Group, Token, TestContext.Current.CancellationToken)).Should().Be(ClaimStatus.Open, "the Src step applied");
    }

    [Fact]
    public async Task A_controller_that_lost_its_lease_sends_no_claim_step()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var alerts = new List<(string Group, string Code, string Detail)>();
        var claim = new InstanceMoveClaim(
            kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps),
            kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps),
            kit.Generations,
            _ => false,
            (g, c, d) => alerts.Add((g, c, d)));

        (await claim.PendAsync(kit.Group, Token, "inst", null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);
        (await claim.OpenAsync(kit.Group, Token, true, null, TestContext.Current.CancellationToken)).Should().Be(ClaimOutcome.Failed);

        alerts.Should().OnlyContain(a => a.Code == "lease_lost");
        (await kit.RawSrc.HashGetAllAsync(kit.Placement.Claim(RecordSide.Src).Key)).Should().BeEmpty();
    }

    private static (InstanceMoveClaim Claim, List<(string Group, string Code, string Detail)> Alerts) Claim(TwoServerKit kit)
    {
        var alerts = new List<(string Group, string Code, string Detail)>();
        var claim = new InstanceMoveClaim(
            kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps),
            kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps),
            kit.Generations,
            _ => true,
            (g, c, d) => alerts.Add((g, c, d)));
        return (claim, alerts);
    }
}
