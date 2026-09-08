using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorStaleEndpointTests
{
    private const string ClusterNodesWithoutRetiredNodes =
        "07c3 4.195.18.22:8500@18500 myself,master - 0 0 1 connected 0-8191\n" +
        "a1b2 4.195.18.22:8501@18501 master - 0 1 2 connected 8192-16383\n";

    private static readonly DnsEndPoint Seed = new("redis.example.net", 10000);
    private static readonly IPEndPoint Live = new(IPAddress.Parse("4.195.18.22"), 8500);
    private static readonly IPEndPoint Retired = new(IPAddress.Parse("4.195.18.22"), 8502);

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 3, 23, 36, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, _now + dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (_timers.Where(t => t.Due <= target).MinBy(t => t.Due) is { } next)
            {
                _now = next.Due!.Value;
                next.Fire();
            }
            _now = target;
        }

        private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            public DateTimeOffset? Due { get; private set; } = due;

            public void Fire()
            {
                Due = period > TimeSpan.Zero ? Due + period : null;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync()
            {
                Due = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SequenceFactory(params IConnectionMultiplexer[] multiplexers) : IConnectionMultiplexerFactory
    {
        private readonly Queue<IConnectionMultiplexer> _multiplexers = new(multiplexers);
        public int CreateCount { get; private set; }
        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return new ValueTask<IConnectionMultiplexer>(_multiplexers.Dequeue());
        }
    }

    private sealed class RecordingTelemetry : ICachingTelemetryProvider
    {
        public List<string> Events { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) => Exceptions.Add(ex);
        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) => Events.Add(eventName);
    }

#pragma warning disable CS0618 // the replacement constructor takes RedisErrorKind, which is still experimental (SER007)
    private static RedisServerException ServerError(string message) => new(message);
#pragma warning restore CS0618

    private sealed class Harness
    {
        public AdvancingTimeProvider Clock { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public IConnectionMultiplexer Multiplexer { get; } = Substitute.For<IConnectionMultiplexer>();
        public IConnectionMultiplexer Replacement { get; } = Substitute.For<IConnectionMultiplexer>();
        public IServer LiveServer { get; } = Server(Live, connected: true);
        public IServer RetiredServer { get; } = Server(Retired, connected: false);
        public SequenceFactory Factory { get; }
        public RedisConnector Connector { get; }
        public TaskCompletionSource OldDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(string clusterNodes = ClusterNodesWithoutRetiredNodes, bool retiredIsConfigured = false, bool replacementAvailable = true, bool timerDriven = false)
        {
            Multiplexer.GetEndPoints(true).Returns(retiredIsConfigured ? [Seed, Retired] : [Seed]);
            Multiplexer.GetServers().Returns([LiveServer, RetiredServer]);
            Multiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
            Multiplexer.When(m => m.Dispose()).Do(_ => OldDisposed.TrySetResult());
            LiveServer.ClusterNodesRawAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult<string?>(clusterNodes));

            Factory = replacementAvailable ? new SequenceFactory(Multiplexer, Replacement) : new SequenceFactory(Multiplexer);
            var options = Options.Create(new RedisConnectionOptions
            {
                ConnectionString = "redis.example.net:10000",
                EnableHangDetection = false,
                EnableStaleEndpointDetection = timerDriven, // otherwise the test calls the scan itself
                StaleEndpointThreshold = TimeSpan.FromMinutes(5),
                StaleEndpointScanInterval = TimeSpan.FromSeconds(30),
            });
            var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
            Connector = new RedisConnector(Telemetry, optionsProvider, Factory, options, configurators: null, clock: Clock);
        }

        public async Task ScanTwiceAcrossThresholdAsync()
        {
            await Connector.ConnectAsync(TestContext.Current.CancellationToken);
            await Connector.ScanStaleEndpointsAsync();
            Clock.Advance(TimeSpan.FromMinutes(5));
            await Connector.ScanStaleEndpointsAsync();
        }

        private static IServer Server(EndPoint endPoint, bool connected)
        {
            var server = Substitute.For<IServer>();
            server.EndPoint.Returns(endPoint);
            server.IsConnected.Returns(connected);
            return server;
        }
    }

    [Fact]
    public async Task Scan_ForcesReconnect_WhenDiscoveredEndpointLeftClusterAndStayedDownPastThreshold()
    {
        var h = new Harness();

        await h.ScanTwiceAcrossThresholdAsync();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().ContainInOrder("Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Timer_DrivesTheScan_ThroughTheInjectedClock()
    {
        var h = new Harness(timerDriven: true);
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);

        h.Clock.Advance(TimeSpan.FromMinutes(5)); // first tick at 30 s records the outage; 4.5 min later it is not yet overdue
        h.Factory.CreateCount.Should().Be(1);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().ContainInOrder("Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNothing_BeforeThreshold()
    {
        var h = new Harness();
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);

        await h.Connector.ScanStaleEndpointsAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().BeEmpty();
        await h.LiveServer.DidNotReceive().ClusterNodesRawAsync(Arg.Any<CommandFlags>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNothing_WhenDownEndpointIsStillAClusterMember()
    {
        var h = new Harness(ClusterNodesWithoutRetiredNodes + "c3d4 4.195.18.22:8502@18502 slave 07c3 0 1 1 disconnected\n");

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_IgnoresConfiguredEndpoints()
    {
        var h = new Harness(retiredIsConfigured: true);

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        await h.LiveServer.DidNotReceive().ClusterNodesRawAsync(Arg.Any<CommandFlags>());
        h.Connector.Dispose();
    }

    [Theory]
    [InlineData("ERR This instance has cluster support disabled")]
    [InlineData("ERR unknown command 'CLUSTER'")]
    public async Task Scan_DoesNothing_WhenServerIsNotACluster(string error)
    {
        var h = new Harness();
        h.LiveServer.ClusterNodesRawAsync(Arg.Any<CommandFlags>())
            .Returns<string?>(_ => throw ServerError(error));

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_TracksOtherServerErrors_WithoutReconnecting()
    {
        var h = new Harness();
        h.LiveServer.ClusterNodesRawAsync(Arg.Any<CommandFlags>())
            .Returns<string?>(_ => throw ServerError("NOPERM this user has no permissions to run the 'cluster|nodes' command"));

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().ContainSingle().Which.Should().BeOfType<RedisServerException>();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_RestartsTheClock_WhenEndpointReconnectsInBetween()
    {
        var h = new Harness();
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);

        await h.Connector.ScanStaleEndpointsAsync();
        h.RetiredServer.IsConnected.Returns(true);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Connector.ScanStaleEndpointsAsync();
        h.RetiredServer.IsConnected.Returns(false);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task Scan_DoesNothing_WhenClusterNodesReplyIsUnusable(string? reply)
    {
        var h = new Harness();
        h.LiveServer.ClusterNodesRawAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult(reply));

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNotReconnectAgain_WhenMultiplexerWasSwappedDuringMembershipCheck()
    {
        var h = new Harness();
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.LiveServer.ClusterNodesRawAsync(Arg.Any<CommandFlags>()).Returns(reply.Task);
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);
        await h.Connector.ScanStaleEndpointsAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var scan = h.Connector.ScanStaleEndpointsAsync();
        h.Connector.ForceReconnect();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        reply.SetResult(ClusterNodesWithoutRetiredNodes);
        await scan;

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().Equal("Redis.ForcedReconnect");
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_RetriesOnNextScan_WhenRebuildFails()
    {
        var h = new Harness(replacementAvailable: false);

        await h.ScanTwiceAcrossThresholdAsync();
        for (var i = 0; i < 500 && h.Telemetry.Exceptions.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Exceptions.Should().ContainSingle();
        await Task.Delay(100, TestContext.Current.CancellationToken); // let the failed rebuild release its guard

        await h.Connector.ScanStaleEndpointsAsync();
        for (var i = 0; i < 500 && h.Telemetry.Exceptions.Count < 2; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        h.Factory.CreateCount.Should().Be(3);
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointDetected", "Redis.StaleEndpointDetected");
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_IsNoOp_BeforeFirstConnect()
    {
        var h = new Harness();

        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(0);
        h.Connector.Dispose();
    }

    [Fact]
    public void ParseClusterNodeAddresses_ReadsIpAndHostname_SkipsNoAddr()
    {
        const string nodes =
            "07c3 10.0.0.1:6379@16379,node-a.internal myself,master - 0 0 1 connected 0-5460\n" +
            "a1b2 10.0.0.2:6379@16379 master - 0 1 2 connected 5461-10922\n" +
            "c3d4 10.0.0.3:6379@16379,node-c.internal,shard-id=abc master - 0 1 3 connected 10923-16383\n" +
            "e5f6 10.0.0.4:6379@16379,,shard-id=def master - 0 1 4 connected\n" +
            "0789 2001:0db8:0:0:0:0:0:1:6379@16379 master - 0 1 5 connected\n" +
            "dead :0@0 master,fail,noaddr - 0 0 0 disconnected\n" +
            "\n";

        var addresses = RedisConnector.ParseClusterNodeAddresses(nodes);

        addresses.Should().BeEquivalentTo(["10.0.0.1:6379", "node-a.internal:6379", "10.0.0.2:6379", "10.0.0.3:6379", "node-c.internal:6379", "10.0.0.4:6379", "2001:db8::1:6379"]);
        addresses.Should().Contain(RedisConnector.FormatEndPoint(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6379)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseClusterNodeAddresses_IsEmpty_ForUnusableReplies(string? reply)
    {
        RedisConnector.ParseClusterNodeAddresses(reply).Should().BeEmpty();
    }

    [Fact]
    public void FormatEndPoint_MatchesClusterNodesAddressShape()
    {
        RedisConnector.FormatEndPoint(Live).Should().Be("4.195.18.22:8500");
        RedisConnector.FormatEndPoint(Seed).Should().Be("redis.example.net:10000");
    }
}
