using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorStaleEndpointTests
{
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

    private sealed class FakeTopology : IClusterTopologyReader
    {
        private object _configuration = new();

        public HashSet<EndPoint>? Members { get; set; }
        public bool RefreshLands { get; set; } = true;
        public int Reads { get; private set; }

        public object? GetConfiguration(IServer server) => Members is null ? null : _configuration;

        public HashSet<EndPoint> GetMembers(object configuration)
        {
            Reads++;
            return Members!;
        }

        public Task<bool> Refreshed()
        {
            if (RefreshLands)
            {
                _configuration = new();
            }

            return Task.FromResult(true);
        }
    }

    private sealed class Harness
    {
        public AdvancingTimeProvider Clock { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public IConnectionMultiplexer Multiplexer { get; } = Substitute.For<IConnectionMultiplexer>();
        public IConnectionMultiplexer Replacement { get; } = Substitute.For<IConnectionMultiplexer>();
        public IServer LiveServer { get; }
        public IServer RetiredServer { get; } = Server(Retired, connected: false);
        public FakeTopology Topology { get; } = new();
        public SequenceFactory Factory { get; }
        public RedisConnector Connector { get; }
        public TaskCompletionSource OldDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(bool retiredIsMember = false, bool clusterConfigurationKnown = true, bool retiredIsConfigured = false, bool replacementAvailable = true, bool timerDriven = false, bool connectedNodeIsConfigured = true)
        {
            LiveServer = Server(connectedNodeIsConfigured ? Seed : Live, connected: true); // only the configured endpoints are re-handshaked by the refresh
            Topology.Members = clusterConfigurationKnown ? (retiredIsMember ? [Live, Retired] : [Live]) : null;
            Multiplexer.GetEndPoints(true).Returns(retiredIsConfigured ? [Seed, Retired] : [Seed]);
            Multiplexer.GetServers().Returns([LiveServer, RetiredServer]);
            Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => Topology.Refreshed());
            Multiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
            Multiplexer.When(m => m.Dispose()).Do(_ => OldDisposed.TrySetResult());

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
            Connector = new RedisConnector(Telemetry, optionsProvider, Factory, options, configurators: null, clock: Clock, topologyReader: Topology);
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
        await h.Multiplexer.Received(1).ConfigureAsync(Arg.Any<TextWriter?>());
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
        await h.Multiplexer.DidNotReceive().ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_ReportsAStillListedMemberOnce_AndAsksAgainOnlyAfterTheThreshold()
    {
        var h = new Harness(retiredIsMember: true);

        await h.ScanTwiceAcrossThresholdAsync();
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointStillAMember");
        for (var i = 0; i < 4; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync();
        }
        await h.Multiplexer.Received(1).ConfigureAsync(Arg.Any<TextWriter?>());

        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointStillAMember");
        h.Telemetry.Exceptions.Should().BeEmpty();
        await h.Multiplexer.Received(2).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_ForgetsAConfirmedMember_OnceItReconnects()
    {
        var h = new Harness(retiredIsMember: true);

        await h.ScanTwiceAcrossThresholdAsync();
        h.RetiredServer.IsConnected.Returns(true);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();
        h.RetiredServer.IsConnected.Returns(false);
        await h.ScanTwiceAcrossThresholdAsync();

        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointStillAMember", "Redis.StaleEndpointStillAMember");
        await h.Multiplexer.Received(2).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_ReconnectsAfterAll_WhenAConfirmedMemberIsLaterRemoved()
    {
        var h = new Harness(retiredIsMember: true);

        await h.ScanTwiceAcrossThresholdAsync();
        h.Topology.Members = [Live];
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Connector.ScanStaleEndpointsAsync();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointStillAMember", "Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_IgnoresConfiguredEndpoints()
    {
        var h = new Harness(retiredIsConfigured: true);

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        await h.Multiplexer.DidNotReceive().ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DisablesItself_OnlyAfterANodeKeepsReportingNoConfiguration()
    {
        var h = new Harness(clusterConfigurationKnown: false);

        await h.ScanTwiceAcrossThresholdAsync();
        for (var refreshes = 1; refreshes < RedisConnector.NullTopologyRefreshLimit; refreshes++)
        {
            h.Telemetry.Events.Should().BeEmpty();
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync();
        }
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointScanDisabled");
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Exceptions.Should().BeEmpty();
        await h.Multiplexer.Received(RedisConnector.NullTopologyRefreshLimit).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Timer_StopsFiring_OnceTheScanIsDisabled()
    {
        var h = new Harness(clusterConfigurationKnown: false, timerDriven: true);
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);

        h.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30) * RedisConnector.NullTopologyRefreshLimit); // the first refresh is at 5:30, one more per tick
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointScanDisabled");
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        h.Factory.CreateCount.Should().Be(1);
        await h.Multiplexer.Received(RedisConnector.NullTopologyRefreshLimit).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_RetriesNextInterval_WhenTheRefreshWasSkipped()
    {
        var h = new Harness();
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => Task.FromResult(false), _ => h.Topology.Refreshed());

        await h.ScanTwiceAcrossThresholdAsync();
        h.Topology.Reads.Should().Be(0);
        h.Telemetry.Events.Should().BeEmpty();

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().ContainInOrder("Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNotJudge_WhenOnlyDiscoveredNodesAreConnected()
    {
        var h = new Harness(connectedNodeIsConfigured: false);

        await h.ScanTwiceAcrossThresholdAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Topology.Reads.Should().Be(0);
        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().BeEmpty();
        await h.Multiplexer.DidNotReceive().ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNotJudge_UntilTheRefreshReplacesTheConfiguration()
    {
        var h = new Harness();
        h.Topology.RefreshLands = false;

        await h.ScanTwiceAcrossThresholdAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Topology.Reads.Should().Be(0);
        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().BeEmpty();
        await h.Multiplexer.Received(2).ConfigureAsync(Arg.Any<TextWriter?>());

        h.Topology.RefreshLands = true;
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().ContainInOrder("Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_DoesNotJudge_WhenTheRefreshedNodeDroppedAfterInstallingATopology()
    {
        var h = new Harness();
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(async _ =>
        {
            await h.Topology.Refreshed();
            h.LiveServer.IsConnected.Returns(false);
            return true;
        });

        await h.ScanTwiceAcrossThresholdAsync();

        h.Factory.CreateCount.Should().Be(1);
        h.Topology.Reads.Should().Be(0);
        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_StartsTheNullStreakOver_OnARebuiltMultiplexer()
    {
        var h = new Harness(clusterConfigurationKnown: false);
        h.Replacement.GetEndPoints(true).Returns([Seed]);
        h.Replacement.GetServers().Returns([h.LiveServer, h.RetiredServer]);
        h.Replacement.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => h.Topology.Refreshed());

        await h.ScanTwiceAcrossThresholdAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync(); // two null refreshes on the first multiplexer
        h.Connector.ForceReconnect();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        for (var i = 0; i < 500 && !h.Replacement.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IConnectionMultiplexer.ConfigureAsync)); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken); // the rebuild releases its guard just after disposing the old multiplexer
            await h.Connector.ScanStaleEndpointsAsync();
        }

        for (var refreshes = 1; refreshes < RedisConnector.NullTopologyRefreshLimit; refreshes++)
        {
            h.Telemetry.Events.Should().Equal("Redis.ForcedReconnect");
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync();
        }

        h.Telemetry.Events.Should().Equal("Redis.ForcedReconnect", "Redis.StaleEndpointScanDisabled");
        await h.Replacement.Received(RedisConnector.NullTopologyRefreshLimit).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_BacksOffExponentially_WhileTheRefreshKeepsFailing_AndRecovers()
    {
        var h = new Harness();
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns<Task<bool>>(_ => throw new RedisTimeoutException(CommandFlags.None, "slow", CommandStatus.Unknown));

        await h.ScanTwiceAcrossThresholdAsync(); // attempt 1; the next two intervals are skipped
        for (var i = 0; i < 11; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync(); // attempts 2 and 3 land after 2 and then 4 skipped intervals
        }

        h.Telemetry.Exceptions.Should().HaveCount(3).And.AllBeOfType<RedisTimeoutException>();
        await h.Multiplexer.Received(3).ConfigureAsync(Arg.Any<TextWriter?>());
        h.Telemetry.Events.Should().BeEmpty();

        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => h.Topology.Refreshed());
        for (var i = 0; i < 8 && !h.Telemetry.Events.Contains("Redis.StaleEndpointDetected"); i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync(); // the remaining skipped intervals run out, then the refresh lands; the event is recorded before the rebuild is queued
        }
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        h.Factory.CreateCount.Should().Be(2);
        h.Telemetry.Events.Should().ContainInOrder("Redis.StaleEndpointDetected", "Redis.ForcedReconnect");
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_ResetsTheBackoff_AfterARefreshSucceeds()
    {
        var h = new Harness(retiredIsMember: true);
        var fail = true;
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => fail ? throw new RedisTimeoutException(CommandFlags.None, "slow", CommandStatus.Unknown) : h.Topology.Refreshed());

        await h.ScanTwiceAcrossThresholdAsync(); // fails once, skips two intervals
        fail = false;
        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync();
        }
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointStillAMember");

        fail = true;
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Connector.ScanStaleEndpointsAsync(); // no skip left over from the earlier failure
        h.Telemetry.Exceptions.Should().HaveCount(2);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Connector.ScanStaleEndpointsAsync(); // skipped: the streak restarted at one failure
        h.Telemetry.Exceptions.Should().HaveCount(2);
        h.Connector.Dispose();
    }

    [Fact]
    public async Task Scan_WaitsForAConnectedNode_BeforeJudgingTheConfiguration()
    {
        var h = new Harness(clusterConfigurationKnown: false);
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ =>
        {
            h.LiveServer.IsConnected.Returns(false); // the refresh itself dropped the last connection
            return Task.FromResult(true);
        });

        await h.ScanTwiceAcrossThresholdAsync();

        h.Telemetry.Events.Should().BeEmpty();
        h.Telemetry.Exceptions.Should().BeEmpty();
        h.Topology.Reads.Should().Be(0);

        h.LiveServer.IsConnected.Returns(true);
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(_ => h.Topology.Refreshed());
        for (var refreshes = 0; refreshes < RedisConnector.NullTopologyRefreshLimit; refreshes++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.Connector.ScanStaleEndpointsAsync();
        }

        h.Factory.CreateCount.Should().Be(1);
        h.Telemetry.Events.Should().Equal("Redis.StaleEndpointScanDisabled");
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scan_DoesNotReconnectAgain_WhenMultiplexerWasSwappedDuringMembershipCheck(bool clusterConfigurationKnown)
    {
        var h = new Harness(clusterConfigurationKnown: clusterConfigurationKnown);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Multiplexer.ConfigureAsync(Arg.Any<TextWriter?>()).Returns(refresh.Task);
        await h.Connector.ConnectAsync(TestContext.Current.CancellationToken);
        await h.Connector.ScanStaleEndpointsAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var scan = h.Connector.ScanStaleEndpointsAsync();
        h.Connector.ForceReconnect();
        await h.OldDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await h.Topology.Refreshed();
        refresh.SetResult(true);
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
    public void FormatEndPoint_MatchesClusterNodesAddressShape()
    {
        RedisConnector.FormatEndPoint(Live).Should().Be("4.195.18.22:8500");
        RedisConnector.FormatEndPoint(Seed).Should().Be("redis.example.net:10000");
    }
}
