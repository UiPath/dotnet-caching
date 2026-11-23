using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Through the leader's loop: a state that lost its commit marker after nodes acted is completed from the control records, never abandoned.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class CommitMismatchTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_commit_marker_is_completed_by_the_leader_under_the_same_identity_even_when_the_flag_asks_for_less(bool useScripts)
    {
        await using var kit = await DriverKit.CreateAsync(fixture, useScripts, withMonitor: true);
        await kit.PrepareAsync();
        var moving = await kit.StepUntilAsync("S3");
        await kit.Kit.Db.HashSetAsync(kit.Kit.StateKey, "dur", "0");
        kit.Fleet.All(n => n.Fl = 2);
        await kit.Monitor!.StepAsync(kit.Group, CancellationToken.None);
        kit.Monitor.RulesFor(kit.Group).Alerts.Should().Contain("commit_mismatch");

        var outcome = await kit.StepAsync();

        outcome!.Plan.Kind.Should().Be(StepKind.Resume);
        var state = await kit.StateAsync();
        state.Should().Match<StateRecord>(s => s.State == "S3" && s.Ep == moving.Ep && s.Id == moving.Id && s.Durable, "completed, not abandoned and not replaced");
        kit.Alerts.Should().Contain(a => a.Code == "commit_mismatch");
        await kit.Monitor.StepAsync(kit.Group, CancellationToken.None);
        kit.Monitor.RulesFor(kit.Group).Alerts.Should().NotContain("commit_mismatch");
    }
}
