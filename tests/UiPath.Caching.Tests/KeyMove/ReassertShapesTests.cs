using System.Globalization;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// A Reassert from the records into each state takes the full field set and leaves the residue unknown, so the source fallback stays on.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ReassertShapesTests(RedisContainerFixture fixture)
{
    private static readonly ClaimFields Claim = new("rt", "tok", "tok:9:S0");

    [Theory]
    [InlineData("S1", "fwd", "0")]
    [InlineData("S3", "fwd", "1")]
    [InlineData("S4", "fwd", "1")]
    [InlineData("S5", "fwd", "1")]
    [InlineData("S0", "none", "0")]
    [InlineData("S3", "rev", "1")]
    public async Task Reassert_into_a_state_derives_nPop_and_moveId_clears_what_it_must_and_holds(string stage, string direction, string np)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, new ControlRecord(40, "rid", stage, direction, np, "S1", "fwd", "0", "39", "before", [("a", "n"), ("b", "n")]));
        await kit.Scripts.ControlCommitAsync(control, 40, "rid");

        var result = await kit.Recovery.ReassertAsync(kit.Group, "operator confirmed", null, null, Claim, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == stage && s.Direction == direction && s.NPop == int.Parse(np, CultureInfo.InvariantCulture) && s.Ep == 41 && s.MoveId == 41 && s.Caches == "a+b" && s.HoldOp == "reassert" && s.Durable);
        state.AllAck.Should().BeEmpty();
        state.Swept.Should().BeEmpty("an S5 group is swept again");
        state.Residue.Should().BeNullOrEmpty("the counts are unknown");
        state.KeepsSourceFallback("a").Should().BeTrue();
        state.Field(StateFields.CopiedAt).Should().BeEmpty();
        state.Field(StateFields.DoneAt).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reasserted_S4_group_cannot_close_until_an_operator_resumes_and_a_inventory_stores_the_counts()
    {
        var state = State("S4", ep: 41, nPop: 1, caches: "a+b", prev: "S3:fwd:1:40:id40", holdOp: "reassert").Approved(ApprovalKinds.Close);

        var held = await StepPlanner.DecideAsync(Input(state), Defaults);

        held.Kind.Should().NotBe(StepKind.Transit, "hold:op keeps stage advancement paused and the residue is unknown");
        held.AlertList.Should().Contain(a => a.Code == "held");
    }
}
