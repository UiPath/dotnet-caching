using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// DR-1: the move claim of a router group at X1, its release into S0, and X11 (the operator's abandon of a closed group), with the claim behind a fake.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverClaimTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task X1_of_a_router_group_takes_the_claim_pend_before_TRANSIT_and_open_before_COMMIT_and_stores_the_token_and_the_descriptor()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();

        var ready = await kit.StepUntilAsync("S1");

        kit.Claim.Calls.Should().Equal("pend:rt", "open");
        ready.Claim.Should().NotBeEmpty();
        ready.Move.Should().Be("rt");
    }

    [Fact]
    public async Task A_refused_pend_starts_nothing_and_a_refused_open_abandons_X1_and_releases_the_pending_claim_idle_with_no_certificate()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        kit.Claim.Pend = ClaimOutcome.Refused;
        await kit.StepAsync();
        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Alerts.Should().Contain(("start_blocked", "claim_held"));
        kit.Claim.Calls.Should().Equal("pend:rt");

        kit.Claim.Pend = ClaimOutcome.Done;
        kit.Claim.Open = ClaimOutcome.Refused;
        kit.Claim.Calls.Clear();
        var outcome = await kit.StepAsync();

        outcome!.Plan.Spec!.Code.Should().Be("X1");
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S0" && s.Durable);
        kit.Claim.Calls.Should().Equal("pend:rt", "open", "idle");
        after.Claim.Should().BeEmpty("a claim that never opened has no certificate and its token is dropped");
        after.Release.Should().BeEmpty();
    }

    [Fact]
    public async Task X6_into_S0_certifies_the_claim_before_DURABLE_copies_the_certificate_into_the_state_and_releases_idle_after()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[0].Fl = 2;
        kit.Claim.Calls.Clear();

        var off = await kit.StepUntilAsync("S0", "none", advance: Ten);

        kit.Claim.Calls.Should().Equal("cert:S0", "idle");
        off.Release.Should().Be($"{ready.Claim}:{off.Ep}:S0");
    }

    [Fact]
    public async Task A_claim_that_cannot_be_certified_holds_the_transition_into_S0_and_it_is_retried()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[0].Fl = 2;
        kit.Claim.Certify = ClaimOutcome.Failed;

        for (var n = 0; n < 14; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).Durable.Should().BeFalse("DURABLE is not written without the certificate");
        kit.Claim.Certify = ClaimOutcome.Done;
        await kit.StepUntilAsync("S0", "none", advance: Ten);
    }

    [Fact]
    public async Task A_group_without_a_claim_never_calls_the_claim()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[0].Fl = 2;
        await kit.StepUntilAsync("S0", "none", advance: Ten);

        kit.Claim.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_abandon_of_a_closed_group_flips_to_S0_waits_for_every_ack_and_recycle_then_purges_and_releases_the_claim()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        kit.Facts.AcceptRefill = true;
        var closed = await ToClosedAsync(kit);
        kit.Claim.Calls.Clear();

        (await kit.Commands.AbandonAsync(kit.Group, "every pod was recycled; a cold start is accepted", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        var flipped = await kit.StepUntilAsync("S0", "none");
        flipped.Should().Match<StateRecord>(s => s.NPop == 0 && s.Ep > closed.Ep && s.Field(StateFields.ColdStart) == closed.Claim);

        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync();
        }

        kit.Data.Calls.Should().NotContain("purge_l", "no pod has recycled its connections after the flip");
        kit.Alerts.Should().Contain(a => a.Code == "stage_pending" && a.Detail.StartsWith("recycle:", StringComparison.Ordinal));
        kit.Fleet.All(n => n.Recycles = true);
        kit.Data.Calls.Clear();

        await kit.StepAsync();

        kit.Data.Calls.Should().Equal("purge_n", "purge_l");
        kit.Claim.Calls.Should().Equal("cert:S0", "idle");
        var done = await kit.StateAsync();
        done.Field(StateFields.ColdStart).Should().BeEmpty();
        done.Release.Should().Be($"{closed.Claim}:{flipped.Ep}:S0");
    }

    [Fact]
    public async Task The_abandon_reopens_the_claim_first_when_the_sweep_already_stored_lswept()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        kit.Facts.AcceptRefill = true;
        var closed = await ToClosedAsync(kit);
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, closed.Id, [(StateFields.Swept, closed.Id)], []);
        kit.Claim.Calls.Clear();
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);

        await kit.StepUntilAsync("S0", "none");

        kit.Claim.Calls.Should().Equal("reopen");
    }

    [Fact]
    public async Task A_claim_that_cannot_be_reopened_stops_the_abandon_before_the_flip()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        kit.Facts.AcceptRefill = true;
        var closed = await ToClosedAsync(kit);
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, closed.Id, [(StateFields.Swept, closed.Id)], []);
        kit.Claim.Reopen = ClaimOutcome.Refused;
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);

        await kit.StepAsync();

        (await kit.StateAsync()).State.Should().Be("S5");
        kit.Alerts.Should().Contain(a => a.Code == "abandon_refused");
    }

    [Fact]
    public async Task A_group_with_a_cache_that_does_not_accept_a_refill_is_never_abandoned()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var closed = await ToClosedAsync(kit);
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S5" && s.Ep == closed.Ep);
        kit.Alerts.Should().Contain(a => a.Code == "abandon_refused");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_abandon_of_X1_certifies_an_open_claim_and_only_releases_a_pending_one(bool open)
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        var off = await kit.StateAsync();
        var target = StateKit.TargetFrom(off, "S1", "fwd", 0) with { Claim = "tok", Move = "rt", MoveId = off.Ep + 1 };
        await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateKit.Expecting(off), target);
        kit.Claim.ForcedStatus = open ? ClaimStatus.Open : ClaimStatus.Pending;
        kit.Fleet.Nodes[0].Fl = 2;

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Abandon);
        var after = await kit.StateAsync();
        after.Should().Match<StateRecord>(s => s.State == "S0" && s.Durable);
        if (open)
        {
            kit.Claim.Calls.Should().Equal("cert:S0", "idle");
            after.Release.Should().Be($"tok:{after.Ep}:S0");
            after.Claim.Should().Be("tok");
        }
        else
        {
            kit.Claim.Calls.Should().Equal("idle");
            after.Release.Should().BeEmpty();
            after.Claim.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Every_claim_call_carries_the_lease_fence()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        var key = kit.Kit.Names.Lead(kit.Group);
        var lease = new LeaderLease(kit.Kit.Scripts, key, kit.Kit.Options, kit.Kit.Time);
        await lease.TryHoldAsync();
        kit.Fence = lease.Fence;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[0].Fl = 2;
        await kit.StepUntilAsync("S0", "none", advance: Ten);

        kit.Claim.Calls.Should().NotBeEmpty();
        kit.Claim.Fences.Should().OnlyContain(f => f == lease.Fence);
        await kit.Kit.Db.KeyDeleteAsync(key);
    }

    private static async Task<StateRecord> ToClosedAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Inventory = new Dictionary<string, long> { ["a"] = 0 };
        await kit.StepUntilAsync("S4");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        return await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
    }
}
