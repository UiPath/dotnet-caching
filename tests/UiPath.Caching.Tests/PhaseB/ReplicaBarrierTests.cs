using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// Needs a replica of the server in REDIS_CONNECTION_STRING at REDIS_REPLICA_CONNECTION_STRING, started with --enable-debug-command yes so a test can stop it from acknowledging:
// docker run -d --network host redis:7.4 redis-server --port 6391 --replicaof 127.0.0.1 6379 --enable-debug-command yes
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ReplicaBarrierTests(RedisContainerFixture fixture)
{
    public static TheoryData<string> Repeats => ["ctlsync", "ctlcommit", "gate", "durable", "allack", "transit"];

    [Theory]
    [MemberData(nameof(Repeats))]
    public async Task A_wait_after_a_repeat_waits_for_a_replica_whose_ack_is_delayed(string kind)
    {
        await using var rig = await RigAsync();
        var repeat = await rig.PrepareAsync(kind);
        await rig.Confirmed();

        var held = await rig.WaitedAfterAsync(repeat, TestContext.Current.CancellationToken);

        held.Should().BeTrue($"{kind}: the WAIT stayed pending while the replica could not acknowledge");
    }

    [Theory]
    [MemberData(nameof(Repeats))]
    public async Task A_negative_control_that_repeats_without_writing_returns_at_once_which_is_what_the_harmless_write_prevents(string kind)
    {
        await using var rig = await RigAsync();
        await rig.PrepareAsync(kind);
        await rig.Confirmed();

        var held = await rig.WaitedAfterAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken);

        held.Should().BeFalse($"{kind}: with nothing written the WAIT has nothing to wait for");
    }

    [Fact]
    public async Task A_run_with_a_real_replica_confirms_both_barriers_and_the_state_wait()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromSeconds(3) });
        _ = ReplicaString();
        var init = await kit.InitAsync("a");

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replica_that_does_not_acknowledge_leaves_the_transition_waiting_at_dur_zero_until_it_comes_back()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(300) });
        _ = ReplicaString();
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true");
        var init = await kit.InitAsync("a");
        var stall = await ReplicaStall.StartAsync(replica, 3);

        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
        await stall.FinishedAsync();

        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_resume_after_a_lost_durable_reply_repeats_the_write_and_waits_for_the_replica()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromSeconds(5) });
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true");
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        var stall = await ReplicaStall.StartAsync(replica, 3);

        // The controller crashed after DURABLE executed and before its WAIT: the state is durable, its replication unconfirmed.
        var resume = kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        resume.IsCompleted.Should().BeFalse("the repeated DURABLE write has a WAIT to wait for");
        (await resume).Should().Be(new TransitionResult(TransitionOutcome.Completed));
        await stall.FinishedAsync();
    }

    [Fact]
    public async Task A_stalled_replica_keeps_the_commit_marker_unconfirmed_so_no_record_is_committed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(250) });
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true,syncTimeout=30000,asyncTimeout=30000");
        var init = await kit.InitAsync("a");
        ReplicaStall? stall = null;

        // The epoch sync is confirmed by the real replica; the replica then stops just before the WAIT of the commit marker.
        kit.Topology.Wait = (_, n) =>
        {
            if (n == 3)
            {
                stall = ReplicaStall.StartAsync(replica, 4).GetAwaiter().GetResult();
            }

            return null;
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.ControlAsync(0)).Should().NotContain(e => e.Name == "dur" && e.Value == "1");
        (await kit.Db.HashGetAsync(kit.StateKey, "cmt")).HasValue.Should().BeTrue();
        await stall!.FinishedAsync();

        kit.Topology.Wait = null;
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
    }

    private static string ReplicaString()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_REPLICA_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connection), "Set REDIS_REPLICA_CONNECTION_STRING to a replica of REDIS_CONNECTION_STRING to run.");
        return connection!;
    }

    private async Task<Rig> RigAsync()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var replicaString = ReplicaString();
        var primary = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var replica = await ConnectionMultiplexer.ConnectAsync(replicaString + ",allowAdmin=true");
        var topology = new MultiplexerTopology(primary, string.Empty, useScripts: true, fixture.ServerVersion, expectedReplicasPerMaster: 1);
        var layout = await topology.ReadLayoutAsync(CancellationToken.None);
        layout.Masters.Should().ContainSingle().Which.ConfiguredReplicas.Should().Be(1);
        return new Rig(primary, replica, await topology.OpenAsync(layout.Masters[0].Endpoint, CancellationToken.None));
    }

    private sealed class Rig(ConnectionMultiplexer primary, ConnectionMultiplexer replica, IMasterChannel channel) : IAsyncDisposable
    {
        private readonly List<RedisKey> _made = [];

        public async Task<Func<Task>> PrepareAsync(string kind)
        {
            var key = new RedisKey($"it-pb3a-{kind}-{Guid.NewGuid():N}");
            _made.Add(key);
            const string id = "id-6";
            var record = MoveTestEnvironment.Record(6, id, ("c1", "n1"));
            var target = new StateTarget("S1", "fwd", 0, 6, id, "S0:none:0:5:id-5", 1);
            switch (kind)
            {
                case "ctlsync":
                    await channel.Control.ControlSyncAsync(key, record);
                    return () => channel.Control.ControlSyncAsync(key, record);
                case "ctlcommit":
                    await channel.Control.ControlSyncAsync(key, record);
                    await channel.Control.ControlCommitAsync(key, 6, id);
                    return () => channel.Control.ControlCommitAsync(key, 6, id);
                case "gate":
                    await channel.Control.ControlSyncAsync(key, record);
                    await channel.Control.GateAsync(key, id, "c1", "CLOSED:x");
                    return () => channel.Control.GateAsync(key, id, "c1", "CLOSED:x");
                case "durable":
                    await channel.State.TransitAsync(key, StateExpectation.Absent, target);
                    await channel.State.DurableAsync(key, id);
                    return () => channel.State.DurableAsync(key, id);
                case "allack":
                    await channel.State.TransitAsync(key, StateExpectation.Absent, target);
                    await channel.State.AllAckAsync(key, id);
                    return () => channel.State.AllAckAsync(key, id);
                case "transit":
                    await channel.State.TransitAsync(key, StateExpectation.Absent, target);
                    return () => channel.State.TransitAsync(key, StateExpectation.Absent, target);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        public async Task Confirmed() => (await channel.WaitAsync(1, TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeGreaterThanOrEqualTo(1);

        // The replica stops answering; the repeat runs; the WAIT is started and either is still pending half a second later or has returned.
        public async Task<bool> WaitedAfterAsync(Func<Task> repeat, CancellationToken cancellationToken)
        {
            var stall = await ReplicaStall.StartAsync(replica, 4);
            await repeat();
            var wait = channel.WaitAsync(1, TimeSpan.FromSeconds(5), cancellationToken);
            await Task.WhenAny(wait, Task.Delay(500, cancellationToken));
            var pending = !wait.IsCompleted;
            (await wait).Should().BeGreaterThanOrEqualTo(1);
            await stall.FinishedAsync();
            return pending;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await primary.GetDatabase().KeyDeleteAsync([.. _made]);
            }
            finally
            {
                await channel.DisposeAsync();
                await replica.DisposeAsync();
                await primary.DisposeAsync();
            }
        }
    }
}
