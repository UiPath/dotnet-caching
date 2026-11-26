using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateReviewFixesTests(RedisContainerFixture fixture)
{
    private static readonly KeyMoveStateOptions WithReplica = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public void A_nodes_line_shows_migrating_and_importing_slots()
    {
        const string line = "07c37dfeb235213a872192d90877d0cd55635b91 127.0.0.1:30004 master - 0 1426238317239 4 connected 0-5 [93->-67ed2db8d677e59ec4a4cefb06858cf2a1a89fa1] [77-<-292f8b365bb7edb5e285caf0b7e6ddc7265d2f4f] 100-200";

        ClusterLayout.ParseTransitions(line).Should().Equal(93, 77);
        ClusterLayout.ParseTransitions("07c37dfeb235 127.0.0.1:1 master - 0 0 4 connected 0-5").Should().BeEmpty();
    }

    [Fact]
    public async Task The_cluster_node_text_is_the_line_the_transitions_are_read_from()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connection), "Set REDIS_CLUSTER_CONNECTION_STRING to a Redis Cluster to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(connection!);
        var topology = new MultiplexerTopology(mux, string.Empty, useScripts: true, null, 0);

        var layout = await topology.ReadLayoutAsync(CancellationToken.None);

        layout.Unsettled.Should().BeEmpty();
        mux.GetServers().First().ClusterConfiguration!.Nodes.First().Raw.Should().Contain("connected");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_control_slot_that_is_migrating_is_not_confirmed_until_it_settles(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        var readings = 0;
        kit.Topology.Layout = (real, _) => Interlocked.Increment(ref readings) < 6 ? real.WithUnsettled([0]) : real;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        kit.Topology.Waits.Should().Be(5, "the unsettled slot is left out of the writes, so nothing is waited for until it settles, and then it is written and waited for like any other");
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_control_slot_that_never_settles_leaves_the_transition_stuck_even_when_barriers_may_be_skipped(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.Layout = (real, _) => real.WithUnsettled([0]);

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_state_slot_that_is_migrating_has_its_write_issued_and_waited_for_again(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        var stateSlot = HashSlot.Of(kit.StateKey.ToString());
        var readings = 0;
        kit.Topology.Layout = (real, _) => Interlocked.Increment(ref readings) < 3 ? real.WithUnsettled([stateSlot]) : real;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);

        kit.Topology.Layout = (real, _) => real.WithUnsettled([stateSlot]);
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var stuck = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1), null, CancellationToken.None);
        stuck.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
    }

    [Fact]
    public async Task The_dedicated_connection_waits_longer_than_the_barrier_before_it_times_out()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        ConfigurationOptions? seen = null;
        var topology = new MultiplexerTopology(
            kit.Mux,
            string.Empty,
            useScripts: true,
            null,
            0,
            async options =>
            {
                seen = options;
                return await ConnectionMultiplexer.ConnectAsync(options);
            },
            barrierWait: TimeSpan.FromSeconds(20));

        await using var channel = await topology.OpenAsync((await topology.ReadLayoutAsync(CancellationToken.None)).Masters[0].Endpoint, CancellationToken.None);

        seen!.AsyncTimeout.Should().BeGreaterThanOrEqualTo(25_000);
        seen.SyncTimeout.Should().BeGreaterThanOrEqualTo(25_000);
    }

    [Fact]
    public void A_host_name_and_its_address_with_the_same_port_are_the_same_server()
    {
        EndPointMatch.Same(new DnsEndPoint("localhost", 6379), new IPEndPoint(IPAddress.Loopback, 6379)).Should().BeTrue();
        EndPointMatch.Same(new DnsEndPoint("localhost", 6379), new IPEndPoint(IPAddress.Loopback, 6380)).Should().BeFalse();
        EndPointMatch.Same(new IPEndPoint(IPAddress.Loopback, 6379), new IPEndPoint(IPAddress.Loopback, 6379)).Should().BeTrue();
        EndPointMatch.Same(null, new IPEndPoint(IPAddress.Loopback, 6379)).Should().BeFalse();
        EndPointMatch.Same(new DnsEndPoint("no-such-host.invalid", 6379), new IPEndPoint(IPAddress.Loopback, 6379)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_state_wait_replicas_of_zero_takes_no_barrier_and_reports_no_missing_replica(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 1, StateWaitReplicas = 0 });
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 0;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Waits.Should().Be(0);
        kit.Reports.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_commit_marker_that_no_replica_holds_stops_the_sequence_before_any_record_is_committed(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");

        // TRANSIT and barrier 1 confirm; the commit marker never does.
        kit.Topology.Wait = (_, n) => n <= 2 ? 1 : 0;
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        (await kit.ControlAsync(0)).Should().NotContain(e => e.Name == "dur" && e.Value == "1");
        kit.Topology.Waits.Should().Be(2 + WithReplica.BarrierRetries);
        (await kit.Db.HashGetAsync(kit.StateKey, "cmt")).HasValue.Should().BeTrue("the marker is written and waited for, and kept for the retry");

        kit.Topology.Wait = (_, _) => 1;
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_controller_paused_past_its_lease_is_refused_by_the_server_though_its_own_check_still_passes(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var key = kit.Names.Lead(kit.Group);
        var paused = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        var nextClock = new ManualTimeProvider(kit.Time.GetUtcNow());
        var next = new LeaderLease(kit.Scripts, key, kit.Options, nextClock);
        (await paused.TryHoldAsync()).Should().BeTrue();
        nextClock.Advance(kit.Options.LeaseTtl);
        await kit.Db.KeyExpireAsync(key, TimeSpan.Zero);
        (await next.TryHoldAsync()).Should().BeTrue();
        await kit.Db.KeyPersistAsync(key);
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        // The paused controller's clock still says it holds the lease; the server has it with another token.
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, new SequenceOptions(HoldsLease: () => paused.HoldsLease, Fence: paused.Fence), CancellationToken.None);

        paused.HoldsLease.Should().BeTrue();
        result.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(init.Id);

        var holder = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, new SequenceOptions(Fence: next.Fence), CancellationToken.None);
        holder.Outcome.Should().Be(TransitionOutcome.Completed);
        await next.ReleaseAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_store_fences_the_allack_and_the_automatic_hold_clear_with_the_lease(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        await kit.Store.SetHoldAsync(kit.Group, HoldKind.Automatic, "canary");
        var lease = new LeaderLease(kit.Scripts, kit.Names.Lead(kit.Group), kit.Options, kit.Time);
        var stale = new LeaseFence(kit.Names.Lead(kit.Group), "not-the-holder");
        await lease.TryHoldAsync();

        (await kit.Store.AllAckAsync(kit.Group, init.Id, stale)).Should().BeFalse();
        (await kit.Store.ClearAutomaticHoldAsync(kit.Group, stale)).Should().BeFalse();
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.AllAck == string.Empty && s.HoldAuto == "canary");

        (await kit.Store.AllAckAsync(kit.Group, init.Id, lease.Fence)).Should().BeTrue();
        (await kit.Store.ClearAutomaticHoldAsync(kit.Group, lease.Fence)).Should().BeTrue();
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.AllAck == init.Id && s.HoldAuto == string.Empty);
        await lease.ReleaseAsync();
    }
}
