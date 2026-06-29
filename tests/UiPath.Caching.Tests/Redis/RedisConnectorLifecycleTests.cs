using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorLifecycleTests
{
    private sealed class SequenceFactory : IConnectionMultiplexerFactory
    {
        private readonly Queue<IConnectionMultiplexer> _multiplexers;
        public int CreateCount { get; private set; }
        public SequenceFactory(params IConnectionMultiplexer[] multiplexers) => _multiplexers = new Queue<IConnectionMultiplexer>(multiplexers);
        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return new ValueTask<IConnectionMultiplexer>(_multiplexers.Dequeue());
        }
    }

    private sealed class GatedFactory(IConnectionMultiplexer multiplexer, Task gate) : IConnectionMultiplexerFactory
    {
        public int CreateAsyncCount;
        public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CreateAsyncCount);
            await gate.ConfigureAwait(false);
            return multiplexer;
        }
    }

    private sealed class ThreadCapturingFactory : IConnectionMultiplexerFactory
    {
        private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();

        public ThreadCapturingFactory()
        {
            _multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Database);
        }

        public IDatabase Database { get; } = Substitute.For<IDatabase>();

        public int? CreateThreadId { get; private set; }

        public SynchronizationContext? CreateSynchronizationContext { get; private set; }

        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            CreateThreadId = Environment.CurrentManagedThreadId;
            CreateSynchronizationContext = SynchronizationContext.Current;
            return new ValueTask<IConnectionMultiplexer>(_multiplexer);
        }
    }

    private sealed class ScriptedFactory(params Func<IConnectionMultiplexer>[] steps) : IConnectionMultiplexerFactory
    {
        private int _index;
        public int CreateCount { get; private set; }
        public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            var step = steps[Math.Min(_index, steps.Length - 1)];
            _index++;
            await Task.Yield();
            return step();
        }
    }

    private sealed class SignalingTelemetry : ICachingTelemetryProvider
    {
        private readonly TaskCompletionSource _exceptionTracked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExceptionTracked => _exceptionTracked.Task;
        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) => _exceptionTracked.TrySetResult();
        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) { }
    }

    private static RedisConnector NewConnector(IConnectionMultiplexerFactory factory, ICachingTelemetryProvider? telemetry = null)
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379", EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        return new RedisConnector(telemetry ?? NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }

    private static RedisConnectionException ConnectFailure() => new(ConnectionFailureType.UnableToConnect, "boom");

    [Fact]
    public async Task Dispose_DisposesMultiplexer_WhenConnected()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var connector = NewConnector(new SequenceFactory(multiplexer));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.Dispose();

        multiplexer.Received(1).Dispose();
    }

    [Fact]
    public void Dispose_IsNoOp_WhenNeverConnected()
    {
        var factory = new SequenceFactory();
        var connector = NewConnector(factory);

        connector.Dispose();

        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public void Database_StartsInitialConnectAwayFromCallerSynchronizationContext()
    {
        var factory = new ThreadCapturingFactory();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var callerContext = new SynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        RedisConnector? connector = null;

        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            connector = NewConnector(factory);

            connector.Database.Should().BeSameAs(factory.Database);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            connector?.Dispose();
        }

        factory.CreateThreadId.Should().NotBe(callerThreadId);
        factory.CreateSynchronizationContext.Should().BeNull();
    }

    [Fact]
    public void IsConnected_False_BeforeConnect_DoesNotTriggerConnect()
    {
        var factory = new SequenceFactory();

        var connector = NewConnector(factory);

        connector.IsConnected.Should().BeFalse();
        connector.GetEndPoints().Should().BeEmpty();
        factory.CreateCount.Should().Be(0);
        connector.Dispose();
    }

    [Fact]
    public async Task IsConnected_ReflectsMultiplexer_AfterConnect()
    {
        var endpoint = new System.Net.DnsEndPoint("localhost", 6379);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetEndPoints(Arg.Any<bool>()).Returns([endpoint]);
        var connector = NewConnector(new SequenceFactory(multiplexer));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.IsConnected.Should().BeTrue();
        connector.GetEndPoints().Should().ContainSingle().Which.Should().Be(endpoint);
        connector.Dispose();
    }

    [Fact]
    public async Task IsConnected_False_WhileInitialConnectInFlight()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        var gate = new TaskCompletionSource();
        var factory = new GatedFactory(multiplexer, gate.Task);
        var connector = NewConnector(factory);

        var warmUp = connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.IsConnected.Should().BeFalse();
        connector.GetEndPoints().Should().BeEmpty();

        gate.SetResult();
        await warmUp;
        connector.Dispose();
    }

    [Fact]
    public void ForceReconnect_IsNoOp_WhenNeverConnected()
    {
        var factory = new SequenceFactory();
        var connector = NewConnector(factory);

        connector.ForceReconnect();

        factory.CreateCount.Should().Be(0);
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_SwapsMultiplexer_ClosesOld_RaisesOnReconnected()
    {
        var oldMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var newMultiplexer = Substitute.For<IConnectionMultiplexer>();
        oldMultiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

        var disposed = new TaskCompletionSource();
        oldMultiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());

        var factory = new SequenceFactory(oldMultiplexer, newMultiplexer);
        var connector = NewConnector(factory);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var reconnectedRaised = false;
        connector.OnReconnected += (_, _) => reconnectedRaised = true;

        connector.ForceReconnect();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        factory.CreateCount.Should().Be(2);
        await oldMultiplexer.Received(1).CloseAsync(true);
        oldMultiplexer.Received(1).Dispose();
        reconnectedRaised.Should().BeTrue();
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_IsNoOp_WhileInitialConnectInFlight()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var gate = new TaskCompletionSource();
        var factory = new GatedFactory(multiplexer, gate.Task);
        var connector = NewConnector(factory);

        var warmUp = connector.ConnectAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && Volatile.Read(ref factory.CreateAsyncCount) == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        connector.ForceReconnect();

        factory.CreateAsyncCount.Should().Be(1);

        gate.SetResult();
        await warmUp;
        connector.Dispose();
    }

    [Theory]
    [InlineData(101, 20000, 3, 20000, true)]
    [InlineData(100, 20000, 3, 20000, false)]
    [InlineData(101, 20000, 1, 20000, false)]
    [InlineData(101, 5000, 3, 20000, false)]
    [InlineData(101, 20000, 3, 5000, false)]
    public void IsHangDetected_EvaluatesThresholds(int awaiting, int sinceWrite, int writeStatus, int sinceRead, bool expected)
    {
        const int now = 1_000_000;
        const int threshold = 15000;

        var result = RedisConnector.IsHangDetected(awaiting, now, now - sinceWrite, writeStatus, now - sinceRead, threshold, threshold);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetConnectionTask_SelfHeals_AfterFaultedConnect()
    {
        var good = Substitute.For<IConnectionMultiplexer>();
        good.IsConnected.Returns(true);
        var factory = new ScriptedFactory(() => throw ConnectFailure(), () => good);
        var connector = NewConnector(factory);

        await Assert.ThrowsAnyAsync<Exception>(async () => await connector.ConnectAsync(TestContext.Current.CancellationToken));

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.IsConnected.Should().BeTrue();
        factory.CreateCount.Should().Be(2);
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_Recovers_FromFaultedConnection()
    {
        var good = Substitute.For<IConnectionMultiplexer>();
        good.IsConnected.Returns(true);
        var factory = new ScriptedFactory(() => throw ConnectFailure(), () => good);
        var connector = NewConnector(factory);

        await Assert.ThrowsAnyAsync<Exception>(async () => await connector.ConnectAsync(TestContext.Current.CancellationToken));

        var reconnected = new TaskCompletionSource();
        connector.OnReconnected += (_, _) => reconnected.TrySetResult();

        connector.ForceReconnect();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.IsConnected.Should().BeTrue();
        factory.CreateCount.Should().Be(2);
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_TracksException_WhenReconnectCreateFails()
    {
        var first = Substitute.For<IConnectionMultiplexer>();
        first.IsConnected.Returns(true);
        var telemetry = new SignalingTelemetry();
        var factory = new ScriptedFactory(() => first, () => throw ConnectFailure());
        var connector = NewConnector(factory, telemetry);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var reconnected = false;
        connector.OnReconnected += (_, _) => reconnected = true;

        connector.ForceReconnect();
        await telemetry.ExceptionTracked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        reconnected.Should().BeFalse();
        connector.IsConnected.Should().BeTrue();
        factory.CreateCount.Should().Be(2);
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_SwallowsOnReconnectedHandlerException()
    {
        var oldMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var newMultiplexer = Substitute.For<IConnectionMultiplexer>();
        oldMultiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var disposed = new TaskCompletionSource();
        oldMultiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());

        var factory = new SequenceFactory(oldMultiplexer, newMultiplexer);
        var connector = NewConnector(factory);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.OnReconnected += (_, _) => throw new InvalidOperationException("handler boom");

        connector.ForceReconnect();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        oldMultiplexer.Received(1).Dispose();
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_DisposesOld_WhenCloseAsyncThrows()
    {
        var oldMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var newMultiplexer = Substitute.For<IConnectionMultiplexer>();
        oldMultiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.FromException(ConnectFailure()));
        var disposed = new TaskCompletionSource();
        oldMultiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());

        var factory = new SequenceFactory(oldMultiplexer, newMultiplexer);
        var connector = NewConnector(factory);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.ForceReconnect();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        oldMultiplexer.Received(1).Dispose();
        connector.Dispose();
    }

    [Fact]
    public async Task Dispose_DisposesMultiplexer_WhenConnectCompletesAfterDispose()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var disposed = new TaskCompletionSource();
        multiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());
        var gate = new TaskCompletionSource();
        var factory = new GatedFactory(multiplexer, gate.Task);
        var connector = NewConnector(factory);

        var warmUp = connector.ConnectAsync(TestContext.Current.CancellationToken);
        connector.Dispose();
        gate.SetResult();
        await warmUp;
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        multiplexer.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectAsync_Throws_AfterDispose()
    {
        var factory = new SequenceFactory();
        var connector = NewConnector(factory);
        connector.Dispose();

        var connect = async () => await connector.ConnectAsync(TestContext.Current.CancellationToken);

        await connect.Should().ThrowAsync<ObjectDisposedException>();
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_Swallows_WhenMultiplexerDisposeThrows()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.When(m => m.Dispose()).Do(_ => throw new InvalidOperationException("dispose boom"));
        var factory = new SequenceFactory(multiplexer);
        var connector = NewConnector(factory);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var dispose = () => connector.Dispose();

        dispose.Should().NotThrow();
        multiplexer.Received(1).Dispose();
    }
}
