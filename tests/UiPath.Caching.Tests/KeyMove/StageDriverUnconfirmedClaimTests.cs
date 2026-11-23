using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverUnconfirmedClaimTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_unconfirmed_claim_open_holds_X1_before_the_commit_and_the_transition_goes_on_once_it_confirms()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        kit.Claim.Open = ClaimOutcome.DoneUnconfirmed;

        for (var n = 0; n < 6; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).Durable.Should().BeFalse("the claim is not on a replica, so the commit does not begin");
        kit.Claim.Calls.Should().Contain("open");

        kit.Claim.Open = ClaimOutcome.Done;
        await kit.StepUntilAsync("S1", advance: Ten);
    }

    [Fact]
    public async Task An_unconfirmed_certificate_does_not_hold_the_transition_into_S0_and_raises_wait_skipped_for_the_claim()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.Claims = true;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[0].Fl = 2;
        kit.Claim.Certify = ClaimOutcome.DoneUnconfirmed;
        kit.Claim.Idle = ClaimOutcome.DoneUnconfirmed;

        await kit.StepUntilAsync("S0", "none", advance: Ten);

        kit.Alerts.Should().Contain(("wait_skipped", "claim"));
    }
}
