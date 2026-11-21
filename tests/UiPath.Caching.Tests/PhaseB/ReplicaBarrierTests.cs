using System.Diagnostics;
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
    private static readonly TimeSpan Stalled = TimeSpan.FromMilliseconds(2000);

    public static TheoryData<string> Repeats => ["ctlsync", "ctlcommit", "gate", "durable", "allack", "transit"];

    [Theory]
    [MemberData(nameof(Repeats))]
    public async Task A_wait_after_a_repeat_waits_for_a_replica_whose_ack_is_delayed(string kind)
    {
        await using var rig = await RigAsync();
        var repeat = await rig.PrepareAsync(kind);
        await rig.Confirmed();

        var elapsed = await rig.WaitedAfterAsync(repeat);

        elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800), kind);
    }

    [Theory]
    [MemberData(nameof(Repeats))]
    public async Task A_negative_control_that_repeats_without_writing_returns_at_once_which_is_what_the_harmless_write_prevents(string kind)
    {
        await using var rig = await RigAsync();
        await rig.PrepareAsync(kind);
        await rig.Confirmed();

        var elapsed = await rig.WaitedAfterAsync(() => Task.CompletedTask);

        elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), kind);
    }

    [Fact]
    public async Task A_run_with_a_real_replica_confirms_both_barriers_and_the_state_wait()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { BarrierWait = TimeSpan.FromSeconds(3) });
        _ = ReplicaString();
        var init = await kit.InitAsync("a");

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replica_that_does_not_acknowledge_leaves_the_transition_waiting_at_dur_zero_until_it_comes_back()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new PhaseBStateOptions { BarrierWait = TimeSpan.FromMilliseconds(300) });
        _ = ReplicaString();
        await using var replica = await ConnectionMultiplexer.ConnectAsync(ReplicaString() + ",allowAdmin=true");
        var init = await kit.InitAsync("a");
        var sleep = Task.Run(() => replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "4"));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
        await sleep;
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
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
        var topology = new MultiplexerTopology(primary, string.Empty, useScripts: true, fixture.ServerVersion);
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

        // The replica stops answering for a while; the repeat runs, then the WAIT is timed.
        public async Task<TimeSpan> WaitedAfterAsync(Func<Task> repeat)
        {
            var sleep = Task.Run(() => replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", Stalled.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await Task.Delay(300);
            await repeat();
            var watch = Stopwatch.StartNew();
            await channel.WaitAsync(1, TimeSpan.FromSeconds(5), CancellationToken.None);
            watch.Stop();
            await sleep;
            await Task.Delay(1200);
            return watch.Elapsed;
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
