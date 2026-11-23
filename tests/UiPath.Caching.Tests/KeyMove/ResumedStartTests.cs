using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The attestations of a Start are spent when its X1 completes, also when it completes on a resume, and never by an X1 that was abandoned.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ResumedStartTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_X1_that_stuck_at_a_barrier_and_completed_on_a_resume_spends_the_attestations_once()
    {
        await using var kit = await StuckAsync();
        kit.Facts.Started.Should().BeEmpty("the transition has not completed");

        kit.Kit.Topology.Wait = (_, _) => 1;
        var outcome = await kit.StepAsync(Ten);
        (await kit.StateAsync()).Field(StateFields.Attested).Should().Be("f:e1,n:e2", "the evidence the resumed Start consumed is recorded in the state");

        outcome!.Plan.Kind.Should().Be(StepKind.Resume);
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S1" && s.Durable);
        kit.Facts.Started.Should().Equal(kit.Group);
    }

    [Fact]
    public async Task An_X1_abandoned_before_the_commit_leaves_the_attestations_unspent()
    {
        await using var kit = await StuckAsync();
        kit.Fleet.All(n => n.Fl = 2);

        for (var n = 0; n < 8 && (await kit.StateAsync()).State != "S0"; n++)
        {
            await kit.StepAsync(TimeSpan.FromSeconds(70));
        }

        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Facts.Started.Should().BeEmpty();
        (await kit.StateAsync()).Field(StateFields.Attested).Should().BeEmpty("an abandoned X1 consumed nothing");
    }

    private async Task<DriverKit> StuckAsync()
    {
        var options = new KeyMoveStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50), EvictionPolicyAttested = true };
        var kit = await DriverKit.CreateAsync(fixture, state: options);
        kit.Facts.Evidence.AddRange(["f:e1", "n:e2"]);
        kit.Kit.Topology.Probe = _ => new MasterProbe(1, "noeviction");
        kit.Kit.Topology.Wait = (_, _) => 0;
        await kit.PrepareAsync();
        for (var n = 0; n < 4 && (await kit.StateAsync()).State == "S0"; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S1" && !s.Durable, "barrier 1 cannot confirm");
        return kit;
    }
}
