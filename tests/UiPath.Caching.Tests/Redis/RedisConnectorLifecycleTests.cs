using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.Availability;
using StackExchange.Redis.Maintenance;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorLifecycleTests
{

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
    public async Task Dispose_CancelsTheTokenAnInFlightConnectWasGiven()
    {
        var factory = new TokenCapturingFactory();
        var connector = NewConnector(factory);
        var connecting = connector.ConnectAsync(TestContext.Current.CancellationToken).AsTask();
        var token = await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        token.IsCancellationRequested.Should().BeFalse();

        connector.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        await connecting.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Dispose_ReportsAFactoryCallbackThatThrowsOnCancellation_RatherThanThrowing()
    {
        var telemetry = new RecordingTelemetryProvider();
        var factory = new TokenCapturingFactory(onToken: token => token.Register(() => throw new InvalidOperationException("callback boom")));
        var connector = NewConnector(factory, telemetry);
        _ = connector.ConnectAsync(TestContext.Current.CancellationToken).AsTask();
        await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var dispose = () => connector.Dispose();

        dispose.Should().NotThrow();
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeOfType<AggregateException>();
    }

    [Fact]
    public async Task Dispose_CancelsTheTokenAnInFlightReconnectWasGiven()
    {
        var factory = new TokenCapturingFactory(first: Substitute.For<IConnectionMultiplexer>());
        var connector = NewConnector(factory);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        connector.ForceReconnect();
        var token = await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_factory_can_still_wait_on_its_token_after_Dispose()
    {
        // Held open, or the factory can finish inline inside Cancel and the source is rightly disposed.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TokenCapturingFactory(holdUntil: release.Task);
        var connector = NewConnector(factory);
        var connecting = connector.ConnectAsync(TestContext.Current.CancellationToken).AsTask();
        var token = await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.Dispose();

        token.WaitHandle.WaitOne(0).Should().BeTrue("a factory still running must see cancellation, not a disposed source");
        release.SetResult();
        await connecting.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Dispose_DisposesTheLifetimeSource_WhenNothingIsConnecting()
    {
        var connector = NewConnector(new SequenceFactory());

        connector.Dispose();

        IsLifetimeDisposed(connector).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_lifetime_source_is_disposed_once_the_last_connect_settles(bool reconnect)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TokenCapturingFactory(first: reconnect ? Substitute.For<IConnectionMultiplexer>() : null, holdUntil: release.Task);
        var connector = NewConnector(factory);
        var connecting = connector.ConnectAsync(TestContext.Current.CancellationToken).AsTask();
        if (reconnect)
        {
            await connecting;
            connector.ForceReconnect();
        }

        await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.Dispose();
        IsLifetimeDisposed(connector).Should().BeFalse("a factory is still running with its token");
        release.SetResult();
        for (var i = 0; i < 500 && !IsLifetimeDisposed(connector); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        IsLifetimeDisposed(connector).Should().BeTrue("the factory has seen the cancellation and returned");
    }

    [Fact]
    public async Task Dispose_DoesNotReportTheReconnectItCancelled()
    {
        var telemetry = new RecordingTelemetryProvider();
        var factory = new TokenCapturingFactory(first: Substitute.For<IConnectionMultiplexer>());
        var connector = NewConnector(factory, telemetry);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        connector.ForceReconnect();
        await factory.Token.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.Dispose();
        var reconnecting = typeof(RedisConnector).GetField("_reconnecting", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 500 && (int)reconnecting.GetValue(connector)! != 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        ((int)reconnecting.GetValue(connector)!).Should().Be(0, "the reconnect has finished");
        telemetry.Exceptions.Should().BeEmpty("a shutdown is not a failed reconnect");
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
    public async Task ForceReconnect_ReachesEveryOnReconnectedHandler_WhenOneThrows()
    {
        // Subscribers re-subscribe from this handler, so a multicast that stops at the first throw detaches the rest.
        var telemetry = new RecordingTelemetryProvider();
        var oldMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var newMultiplexer = Substitute.For<IConnectionMultiplexer>();
        oldMultiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var disposed = new TaskCompletionSource();
        oldMultiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());
        var connector = NewConnector(new SequenceFactory(oldMultiplexer, newMultiplexer), telemetry);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var boom = new InvalidOperationException("handler boom");
        var reached = 0;
        connector.OnReconnected += (_, _) => throw boom;
        connector.OnReconnected += (_, _) => Interlocked.Increment(ref reached);

        connector.ForceReconnect();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Volatile.Read(ref reached).Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(boom);
        connector.Dispose();
    }

    [Fact]
    public async Task ServerMaintenance_ReachesEveryHandler_WhenOneThrows()
    {
        var (connector, multiplexer, telemetry) = await ConnectedAsync();
        var boom = new InvalidOperationException("handler boom");
        var reached = 0;
        connector.ServerMaintenance += (_, _) => throw boom;
        connector.ServerMaintenance += (_, _) => reached++;

        var raise = () => multiplexer.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(multiplexer, MaintenanceEvent());

        raise.Should().NotThrow("this is raised on the client's own dispatch, which must not see our subscribers throw");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(boom);
        connector.Dispose();
    }

    [Fact]
    public async Task OnConnectionFailed_ReachesEveryHandler_WhenOneThrows()
    {
        var (connector, multiplexer, telemetry) = await ConnectedAsync();
        var boom = new InvalidOperationException("handler boom");
        var reached = 0;
        connector.OnConnectionFailed += (_, _) => throw boom;
        connector.OnConnectionFailed += (_, _) => reached++;

        var raise = () => multiplexer.ConnectionFailed += Raise.EventWith(multiplexer, FailedArgs(ConnectionFailureType.SocketFailure));

        raise.Should().NotThrow("this is raised on the client's own dispatch, which must not see our subscribers throw");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(boom);
        connector.Dispose();
    }

    [Fact]
    public async Task OnConnectionRestored_ReachesEveryHandler_WhenOneThrows()
    {
        var (connector, multiplexer, telemetry) = await ConnectedAsync();
        var boom = new InvalidOperationException("handler boom");
        var reached = 0;
        connector.OnConnectionRestored += (_, _) => throw boom;
        connector.OnConnectionRestored += (_, _) => reached++;

        var raise = () => multiplexer.ConnectionRestored += Raise.EventWith(multiplexer, FailedArgs(ConnectionFailureType.SocketFailure));

        raise.Should().NotThrow("this is raised on the client's own dispatch, which must not see our subscribers throw");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(boom);
        connector.Dispose();
    }

    [Fact]
    public async Task Version_FallsBack_WhenReadingItAndReportingTheFailureBothThrow()
    {
        // _version is a Lazy, and a Lazy caches a factory exception for the life of the process -- a sink that
        // refuses the report would make Version throw for good rather than fall back once.
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetEndPoints(Arg.Any<bool>()).Returns(_ => throw ConnectFailure());
        var connector = NewConnector(new SequenceFactory(multiplexer), new ThrowingSinkTelemetryProvider());
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var read = () => connector.Version;

        read.Should().NotThrow();
        read.Should().NotThrow("a Lazy caches what its factory threw, so the second read would throw too");
        connector.Dispose();
    }

    [Fact]
    public async Task ServerMaintenance_IsRoutedByTheSubscribedMultiplexer_NotTheSender()
    {
        // A composite multiplexer can attach our handler to its children and raise with the child as sender, so
        // the generation has to be the connection we subscribed to, not whatever arrives in the argument.
        var (connector, multiplexer, _) = await ConnectedAsync();
        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        var moving = PushEvent();
        multiplexer.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(new object(), moving);

        seen.Should().ContainSingle("a MOVING on the current connection is not dropped because a child raised it")
            .Which.Should().BeSameAs(moving);
        connector.Dispose();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task AMoving_FromAGroup_IsTakenOnlyFromTheActiveMember(bool fromActive, int expected)
    {
        // A group attaches our handler to every member, so the raiser tells us which one saw it -- and only the
        // member carrying commands has a replacement the cache cares about.
        var group = Substitute.For<IConnectionGroup>();
        var activeMember = new object();
        var connector = NewConnector(new SequenceFactory(group));
        connector.SetActiveMemberConnectionResolver(_ => activeMember);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        group.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(fromActive ? activeMember : new object(), PushEvent());

        seen.Should().HaveCount(expected);
        connector.Dispose();
    }

    [Fact]
    public async Task AMoving_FromAGroup_IsTaken_WhenTheActiveMemberCannotBeRead()
    {
        // The member's connection is read reflectively, so an upstream rename has to cost a spare record rather
        // than the handoff notice.
        var telemetry = new RecordingTelemetryProvider();
        var group = Substitute.For<IConnectionGroup>();
        var connector = NewConnector(new SequenceFactory(group), telemetry);
        var boom = new InvalidOperationException("no such property");
        connector.SetActiveMemberConnectionResolver(_ => throw boom);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        group.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(new object(), PushEvent());

        seen.Should().ContainSingle("a check that cannot be made must not drop the notice");
        telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(boom);
        connector.Dispose();
    }

    [Fact]
    public async Task ABroadcast_FromAGroup_IsTaken_WhicheverMemberSawIt()
    {
        // Only a MOVING is scoped to a member; a broadcast may have been seen by just one of them.
        var group = Substitute.For<IConnectionGroup>();
        var connector = NewConnector(new SequenceFactory(group));
        connector.SetActiveMemberConnectionResolver(_ => new object());
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        group.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(new object(), BroadcastPushEvent());

        seen.Should().ContainSingle("a broadcast is not scoped to the member that received it");
        connector.Dispose();
    }

    [Fact]
    public async Task ForceReconnect_Completes_WhenRecordingTheEventThrows()
    {
        // The record sits between the swap and the two things that finish the reconnect, so a refusing sink
        // would leave every subscriber unnotified and the retired connection undisposed.
        var oldMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var newMultiplexer = Substitute.For<IConnectionMultiplexer>();
        oldMultiplexer.CloseAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var disposed = new TaskCompletionSource();
        oldMultiplexer.When(m => m.Dispose()).Do(_ => disposed.TrySetResult());
        var connector = NewConnector(new SequenceFactory(oldMultiplexer, newMultiplexer), new ThrowOnEventTelemetryProvider("Redis.ForcedReconnect"));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var reconnected = 0;
        connector.OnReconnected += (_, _) => Interlocked.Increment(ref reconnected);

        connector.ForceReconnect();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Volatile.Read(ref reconnected).Should().Be(1, "subscribers re-subscribe on this event");
        oldMultiplexer.Received(1).Dispose();
        connector.Dispose();
    }

    [Fact]
    public async Task AHandoff_DoesNotFaultTheClientDispatch_WhenRecordingItThrows()
    {
        // Raised by StackExchange.Redis on its own thread, so nothing here may let a refused record reach it.
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var connector = NewConnector(new SequenceFactory(multiplexer), new ThrowOnEventTelemetryProvider("Redis.MaintenanceHandoff"));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var forwarded = 0;
        connector.OnConnectionFailed += (_, _) => forwarded++;

        var raise = () => multiplexer.ConnectionFailed += Raise.EventWith(multiplexer, FailedArgs(ConnectionFailureType.MaintenanceHandoff));

        raise.Should().NotThrow();
        forwarded.Should().Be(1, "the connection did drop, so subscribers still hear about it");
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

        var warmUp = connector.ConnectAsync(TestContext.Current.CancellationToken).AsTask();
        connector.Dispose();
        gate.SetResult();
        await warmUp.Invoking(t => t).Should().ThrowAsync<ObjectDisposedException>("the connection it made was disposed, not handed back");
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

    [Fact]
    public async Task ServerMaintenance_IsForwarded_FromTheCurrentMultiplexer_AcrossAReconnect()
    {
        // The routing tests substitute IRedisConnector, so nothing else covers the wiring: without this they
        // would all pass while production received nothing.
        var first = Substitute.For<IConnectionMultiplexer>();
        var replacement = Substitute.For<IConnectionMultiplexer>();
        var connector = NewConnector(new SequenceFactory(first, replacement));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        var onFirst = MaintenanceEvent();
        first.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(first, onFirst);
        seen.Should().ContainSingle().Which.Should().BeSameAs(onFirst);

        // Closing the retired multiplexer detaches its handler, and OnReconnected fires just before that -- so
        // without this gate the assertions below race teardown and the first would pass for the wrong reason.
        var closing = new TaskCompletionSource();
        first.CloseAsync(Arg.Any<bool>()).Returns(closing.Task);

        var reconnected = new TaskCompletionSource();
        connector.OnReconnected += (_, _) => reconnected.TrySetResult();
        connector.ForceReconnect();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var onReplacement = MaintenanceEvent();
        replacement.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(replacement, onReplacement);

        seen.Should().HaveCount(2, "the replacement multiplexer must be wired too");
        seen[1].Should().BeSameAs(onReplacement);

        // The positive side of the filter.
        var onCurrent = PushEvent();
        replacement.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(replacement, onCurrent);
        seen.Should().HaveCount(3, "a push frame from the current connection describes the endpoint in use");
        seen[2].Should().BeSameAs(onCurrent);

        first.DidNotReceive().Dispose();

        // Still subscribed, so these reach the filter rather than an absent handler. A retired generation's
        // MOVING names an endpoint the cache has left.
        first.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(first, PushEvent());
        seen.Should().HaveCount(3, "a retired connection's MOVING names an endpoint the cache has left");

        // Broadcast kinds stay: the retired connection may be the only one that observed this.
        var migrating = BroadcastPushEvent();
        first.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(first, migrating);
        seen.Should().HaveCount(4, "a broadcast push frame is not scoped to the connection that received it");
        seen[3].Should().BeSameAs(migrating);

        // Azure's is pub/sub, which the client does not collapse, and the retired connection may be the only
        // one that was subscribed when it went out.
        var broadcast = AzureEvent();
        first.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(first, broadcast);
        seen.Should().HaveCount(5, "an Azure broadcast is true whichever connection received it");
        seen[4].Should().BeSameAs(broadcast);

        closing.SetResult();
        connector.Dispose();
    }

    [Fact]
    public async Task AMoving_IsForwarded_FromTheReplacementBeforeItBecomesCurrent()
    {
        // The replacement is subscribed before it is published, and the server never replays a MOVING -- so one
        // arriving in between has to be taken. Raised from inside the subscription, which is exactly that interval.
        var first = Substitute.For<IConnectionMultiplexer>();
        var replacement = Substitute.For<IConnectionMultiplexer>();
        var moving = PushEvent();
        var raised = false;
        replacement.When(m => m.ServerMaintenanceEvent += Arg.Any<EventHandler<ServerMaintenanceEvent>>())
            .Do(call =>
            {
                if (raised)
                {
                    return;
                }

                raised = true;
                call.Arg<EventHandler<ServerMaintenanceEvent>>().Invoke(replacement, moving);
            });

        var connector = NewConnector(new SequenceFactory(first, replacement));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        var seen = new List<ServerMaintenanceEvent>();
        connector.ServerMaintenance += (_, e) => seen.Add(e);

        var reconnected = new TaskCompletionSource();
        connector.OnReconnected += (_, _) => reconnected.TrySetResult();
        connector.ForceReconnect();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        seen.Should().ContainSingle("the generation about to carry commands is the one a MOVING describes")
            .Which.Should().BeSameAs(moving);

        connector.Dispose();
    }

    [Fact]
    public async Task AMaintenanceHandoff_IsNotReportedAsAConnectionFailure()
    {
        // ConnectionStateMonitorHandoffTests raises on a fake source, so it never reaches this branch.
        var telemetry = new RecordingTelemetryProvider();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var connector = NewConnector(new SequenceFactory(multiplexer), telemetry);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var forwarded = 0;
        connector.OnConnectionFailed += (_, _) => forwarded++;

        multiplexer.ConnectionFailed += Raise.EventWith(multiplexer, FailedArgs(ConnectionFailureType.MaintenanceHandoff));

        forwarded.Should().Be(1, "the connection did drop, so subscribers still need to hear about it");
        telemetry.Events.Should().Contain(e => e.Name == "Redis.MaintenanceHandoff");
        telemetry.Events.Should().NotContain(e => e.Name == "Redis.ConnectionFailed");

        multiplexer.ConnectionFailed += Raise.EventWith(multiplexer, FailedArgs(ConnectionFailureType.SocketFailure));
        telemetry.Events.Should().Contain(e => e.Name == "Redis.ConnectionFailed");

        connector.Dispose();
    }

    [Fact]
    public void GetPrimaries_IsEmpty_BeforeConnect_DoesNotTriggerConnect()
    {
        var factory = new SequenceFactory();

        var connector = NewConnector(factory);

        connector.GetPrimaries().Should().BeEmpty();
        factory.CreateCount.Should().Be(0);
        connector.Dispose();
    }

    [Fact]
    public async Task GetPrimaries_KeepsOnlyConnectedPrimaries_AfterConnect()
    {
        var primary = StubServer(isReplica: false, isConnected: true);
        var replica = StubServer(isReplica: true, isConnected: true);
        var unreachablePrimary = StubServer(isReplica: false, isConnected: false);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetServers().Returns([replica, primary, unreachablePrimary]);
        var connector = NewConnector(new SequenceFactory(multiplexer));
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        // A replica refuses a DemandMaster SCAN and an unreachable node cannot answer one.
        connector.GetPrimaries().Should().ContainSingle().Which.Should().BeSameAs(primary);
        connector.Dispose();
    }

    [Fact]
    public async Task GetPrimaries_IsEmpty_WhileInitialConnectInFlight()
    {
        var primary = StubServer(isReplica: false, isConnected: true);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetServers().Returns([primary]);
        var gate = new TaskCompletionSource();
        var connector = NewConnector(new GatedFactory(multiplexer, gate.Task));

        var warmUp = connector.ConnectAsync(TestContext.Current.CancellationToken);

        connector.GetPrimaries().Should().BeEmpty();

        gate.SetResult();
        await warmUp;
        connector.GetPrimaries().Should().ContainSingle();
        connector.Dispose();
    }

    private static IServer StubServer(bool isReplica, bool isConnected)
    {
        var server = Substitute.For<IServer>();
        server.IsReplica.Returns(isReplica);
        server.IsConnected.Returns(isConnected);
        return server;
    }

    private static ConnectionFailedEventArgs FailedArgs(ConnectionFailureType failureType) =>
        (ConnectionFailedEventArgs)Activator.CreateInstance(
            typeof(ConnectionFailedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [null, null, new System.Net.DnsEndPoint("node", 6379), ConnectionType.Interactive, failureType, null, null],
            null)!;

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    /// <summary>A push frame every node broadcasts, rather than a MOVING.</summary>
    private static PushMaintenanceEvent BroadcastPushEvent() => PushEvent(MaintenanceNotificationType.Migrating);

    private static PushMaintenanceEvent PushEvent(MaintenanceNotificationType type = MaintenanceNotificationType.Moving) =>
        (PushMaintenanceEvent)Activator.CreateInstance(
            typeof(PushMaintenanceEvent),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [type, 1L, (EndPoint)new DnsEndPoint("node", 6379), (TimeSpan?)null, (EndPoint?)null, "payload", $">{type} 1 payload", Array.Empty<ClusterSlotMigration>()],
            null)!;
#pragma warning restore SER010

    private static AzureMaintenanceEvent AzureEvent() =>
        (AzureMaintenanceEvent)Activator.CreateInstance(
            typeof(AzureMaintenanceEvent),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            ["NotificationType|NodeMaintenanceStarting|StartTimeInUTC|2026-09-19T00:00:00|IsReplica|False|IPAddress|127.0.0.1|SSLPort|6380|NonSSLPort|6379"],
            null)!;

    private static ServerMaintenanceEvent MaintenanceEvent() =>
        (ServerMaintenanceEvent)Activator.CreateInstance(
            typeof(ServerMaintenanceEvent), BindingFlags.Instance | BindingFlags.NonPublic, null, null, null)!;

    private static async Task<(RedisConnector Connector, IConnectionMultiplexer Multiplexer, RecordingTelemetryProvider Telemetry)> ConnectedAsync()
    {
        var telemetry = new RecordingTelemetryProvider();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var connector = NewConnector(new SequenceFactory(multiplexer), telemetry);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        return (connector, multiplexer, telemetry);
    }

    private static bool IsLifetimeDisposed(RedisConnector connector)
    {
        var source = (CancellationTokenSource)typeof(RedisConnector).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connector)!;
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static RedisConnector NewConnector(IConnectionMultiplexerFactory factory, ICachingTelemetryProvider? telemetry = null)
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379", EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        return new RedisConnector(telemetry ?? NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }

    private static RedisConnectionException ConnectFailure() => new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "boom");
    /// <summary>A sink that refuses one named event and takes everything else.</summary>
    private sealed class ThrowOnEventTelemetryProvider(string failingEvent) : ICachingTelemetryProvider
    {
        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
        {
            if (eventName == failingEvent)
            {
                throw new InvalidOperationException("sink boom");
            }
        }
    }

    /// <summary>A sink that refuses every report, leaving a catch with nowhere to put what it caught.</summary>
    private sealed class ThrowingSinkTelemetryProvider : ICachingTelemetryProvider
    {
        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
            throw new InvalidOperationException("sink boom");
    }

    private sealed class SequenceFactory : IConnectionMultiplexerFactory
    {
        private readonly Queue<IConnectionMultiplexer> _multiplexers;
        public SequenceFactory(params IConnectionMultiplexer[] multiplexers) => _multiplexers = new Queue<IConnectionMultiplexer>(multiplexers);
        public int CreateCount { get; private set; }
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

    private sealed class TokenCapturingFactory(IConnectionMultiplexer? first = null, Action<CancellationToken>? onToken = null, Task? holdUntil = null) : IConnectionMultiplexerFactory
    {
        private readonly TaskCompletionSource<CancellationToken> _token = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task<CancellationToken> Token => _token.Task;

        public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            if (first is not null && Interlocked.Increment(ref _calls) == 1)
            {
                return first;
            }

            onToken?.Invoke(cancellationToken);
            _token.TrySetResult(cancellationToken);
            if (holdUntil is not null)
            {
                await holdUntil.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
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
}
