using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Needs the replica of ReplicaBarrierTests: REDIS_REPLICA_CONNECTION_STRING, started with --enable-debug-command yes.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ReplicaRecoveryTests(RedisContainerFixture fixture)
{
    private static readonly KeyMoveStateOptions Options = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(250), EvictionPolicyAttested = true };

    [Fact]
    public async Task The_preflight_sees_the_connected_replica_and_a_dedicated_wait_that_answers()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, Options);
        _ = ReplicaString();

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Ok.Should().BeTrue(string.Join(", ", result.Failures.Select(f => f.Detail)));
    }

    [Fact]
    public async Task A_replica_that_stops_acknowledging_blocks_a_transition_until_the_operator_skips_the_barrier()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, Options);
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true,syncTimeout=30000,asyncTimeout=30000");
        var init = await kit.InitAsync("a");
        var stall = await ReplicaStall.StartAsync(replica, 8);

        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        var skipped = await kit.Sequence.ResumeAsync(kit.Group, new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        skipped.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
        await stall.FinishedAsync();
    }

    [Fact]
    public async Task The_way_back_and_an_abandon_do_not_wait_for_a_replica_that_is_down()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, Options);
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true,syncTimeout=30000,asyncTimeout=30000");
        var init = await kit.InitAsync("a");
        var stall = await ReplicaStall.StartAsync(replica, 8);
        var way = new SequenceOptions(WayBack: true, Ledger: kit.Ledger);

        // The first way-back write tries the replica; every barrier after it is skipped at once.
        var first = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), way, CancellationToken.None);
        first.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        var stateNow = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var started = kit.Topology.Waits;
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(stateNow), StateKit.TargetFrom(stateNow, "S3", "fwd", 1));
        var abandon = await kit.Sequence.AbandonAsync(kit.Group, advertAcknowledges: false, updatedAt: 1, null, CancellationToken.None);

        abandon.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true));
        kit.Topology.Waits.Should().Be(started, "an abandon issues no WAIT");
        await stall.FinishedAsync();
    }

    [Fact]
    public async Task Rebuild_recreation_and_reassert_confirm_their_barriers_on_a_healthy_replica()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromSeconds(3) });
        _ = ReplicaString();
        var init = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S3", "fwd", 1), null, CancellationToken.None);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        var rebuilt = await kit.Upkeep.RebuildAsync(kit.Group, state, null, null, CancellationToken.None);
        await kit.Db.KeyDeleteAsync(kit.StateKey);
        var current = new PeerIdentity("S3", "fwd", state.Ep + 1, (await kit.ControlAsync(0)).First(e => e.Name == "id").Value!);
        var recreated = await kit.Recovery.RecreateAsync(kit.Group, [current], [], null, CancellationToken.None);
        var reasserted = await kit.Recovery.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None);

        rebuilt.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        recreated.Transition.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        reasserted.Transition.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Reports.Select(r => r.Kind).Should().NotContain(["wait_skipped", "barrier_failed", "barrier_stuck"]);
    }

    private static string ReplicaString()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_REPLICA_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connection), "Set REDIS_REPLICA_CONNECTION_STRING to a replica of REDIS_CONNECTION_STRING to run.");
        return connection!;
    }
}
