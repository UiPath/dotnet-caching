#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamNotifyChannelTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public async Task Incoming_pubsub_message_signals_waiter()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var captured = new TaskCompletionSource<Action<RedisChannel, RedisValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ci => captured.TrySetResult(ci.Arg<Action<RedisChannel, RedisValue>>()!));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");

        var capturedHandler = await captured.Task.WaitAsync(30.Seconds(), token);

        var waitTask = waiter.WaitAsync(token);
        capturedHandler(channel, RedisValue.EmptyString);

        var completed = await Task.WhenAny(waitTask, Task.Delay(2.Seconds(), token));
        completed.Should().BeSameAs(waitTask);
    }

    [Fact]
    public async Task A_doorbell_rung_by_this_node_does_not_signal_the_waiter()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var captured = new TaskCompletionSource<Action<RedisChannel, RedisValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ci => captured.TrySetResult(ci.Arg<Action<RedisChannel, RedisValue>>()!));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");

        var capturedHandler = await captured.Task.WaitAsync(30.Seconds(), token);
        // The subscribe itself signals once; drain that before ringing.
        await waiter.WaitAsync(token);

        var waitTask = waiter.WaitAsync(token);
        capturedHandler(channel, "self");
        (await Task.WhenAny(waitTask, Task.Delay(300.Milliseconds(), token))).Should().NotBeSameAs(waitTask, "this node's own entries are never dispatched, so its own ring is not worth a read");

        capturedHandler(channel, "other");
        (await Task.WhenAny(waitTask, Task.Delay(2.Seconds(), token))).Should().BeSameAs(waitTask);
    }

    [Fact]
    public async Task Subscribe_failure_is_retried_on_timer()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var calls = 0;
        var twoCalls = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n >= 2)
                {
                    twoCalls.TrySetResult(true);
                }
                if (n == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "boom");
                }
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 50.Milliseconds(), 10.Milliseconds(), "self");
        await twoCalls.Task.WaitAsync(30.Seconds(), token);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Dispose_unsubscribes()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);

        sut.Dispose();

        subscriber.Received().Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Dispose_does_not_dispose_externally_owned_waiter()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(50.Milliseconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);

        sut.Dispose();

        Func<Task> act = async () => await waiter.WaitAsync(token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnReconnected_reschedules_subscribe()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var calls = 0;
        var firstCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    firstCall.TrySetResult(true);
                }

                if (n >= 2)
                {
                    secondCall.TrySetResult(true);
                }
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self");
        await firstCall.Task.WaitAsync(30.Seconds(), token);

        redis.OnReconnected += Raise.Event();

        await secondCall.Task.WaitAsync(30.Seconds(), token);
        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Reconnect_during_a_subscribe_is_not_lost_with_the_timer()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var calls = 0;
        var secondCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    // Mid-attempt, before it disables its own timer: this subscribe is against the old connection.
                    redis.OnReconnected += Raise.Event();
                }
                if (n >= 2)
                {
                    secondCall.TrySetResult(true);
                }
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self");

        await secondCall.Task.WaitAsync(30.Seconds(), token);
        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);

        sut.Dispose();
        Action act = () => sut.Dispose();

        act.Should().NotThrow();
        subscriber.Received(1).Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Dispose_swallows_unsubscribe_errors()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));
        subscriber.When(s => s.Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down"));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);

        Action act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task OnReconnected_is_noop_after_dispose()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);
        sut.Dispose();

        Action act = () => redis.OnReconnected += Raise.Event();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Resubscribe_swallows_previous_unsubscribe_failure_and_resubscribes()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);

        var subscribeCalls = 0;
        var firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref subscribeCalls);
                if (n == 1)
                {
                    firstSubscribed.TrySetResult(true);
                }

                if (n >= 2)
                {
                    secondSubscribed.TrySetResult(true);
                }
            });
        subscriber.When(s => s.Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "stale"));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self");
        await firstSubscribed.Task.WaitAsync(30.Seconds(), token);

        redis.OnReconnected += Raise.Event();

        await secondSubscribed.Task.WaitAsync(30.Seconds(), token);
        Volatile.Read(ref subscribeCalls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData("NOPERM No permissions to access a channel")]
    [InlineData("ERR unknown command 'SUBSCRIBE'")]
    public async Task A_subscription_the_server_refuses_is_warned_about_once_and_not_tried_again(string error)
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var calls = 0;
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                Interlocked.Increment(ref calls);
                throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, error);
            });
        var logger = new RecordingLogger();

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, logger, waiter, 50.Milliseconds(), 10.Milliseconds(), "self");
        await Task.Delay(300, token);
        redis.OnReconnected += Raise.Event();
        await Task.Delay(300, token);

        Volatile.Read(ref calls).Should().Be(1, "the server will refuse it again; this topic polls");
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("polls only"));
        logger.Records.Should().NotContain(r => r.Level == LogLevel.Error);
    }

    [Fact]
    public void The_constructor_does_not_touch_the_connection()
    {
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 60.Seconds(), "self");

        _ = redis.DidNotReceive().Subscriber;
        _ = redis.DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_connection_that_is_not_up_yet_is_awaited_and_tried_again_rather_than_failing_the_topic()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var connects = 0;
        // The first attempt's connect fails; the retry's finds the connection up.
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => Interlocked.Increment(ref connects) == 1
            ? ValueTask.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "It was not possible to connect"))
            : ValueTask.CompletedTask);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>())).Do(_ => subscribed.TrySetResult());

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 50.Milliseconds(), 10.Milliseconds(), "self");

        await subscribed.Task.WaitAsync(30.Seconds(), token);
        Volatile.Read(ref connects).Should().BeGreaterThan(1, "the retry found the connection up and subscribed");
        _ = multiplexer.DidNotReceive().TimeoutMilliseconds;
    }

    [Fact]
    public async Task A_subscription_landing_after_dispose_gave_up_waiting_for_it_is_still_unsubscribed()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()).Returns(_ =>
        {
            started.TrySetResult();
            return pending.Task;
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self");
        await started.Task.WaitAsync(30.Seconds(), token);

        // Dispose waits five seconds for the attempt, then gives up; the server answers after that.
        await Task.Run(sut.Dispose, token);
        pending.SetResult();

        var deadline = DateTime.UtcNow + 10.Seconds();
        while (DateTime.UtcNow < deadline && !subscriber.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISubscriber.Unsubscribe)))
        {
            await Task.Delay(20, token);
        }

        subscriber.Received(1).Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task A_transient_subscribe_failure_is_warned_about_once_and_retried_quietly()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var calls = 0;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                if (Interlocked.Increment(ref calls) <= 2)
                {
                    throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down");
                }

                subscribed.TrySetResult();
            });
        var logger = new RecordingLogger();

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, logger, waiter, 50.Milliseconds(), 10.Milliseconds(), "self");
        await subscribed.Task.WaitAsync(30.Seconds(), token);

        logger.Records.Where(r => r.Message.Contains("subscribe error")).Select(r => r.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
        logger.Records.Should().NotContain(r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Until_the_subscription_is_up_the_poll_interval_applies()
    {
        using var rig = new LiveRig(_fixture, dueTime: 1.Hours());

        (await rig.WaitsLongAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Once_subscribed_the_notified_interval_applies()
    {
        using var rig = new LiveRig(_fixture);

        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_connection_ends_the_long_wait_in_progress_and_the_poll_interval_applies()
    {
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
        var wait = rig.Waiter.WaitAsync(CancellationToken.None);
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse();

        rig.Redis.OnConnectionFailed += Raise.Event();

        await wait.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        (await rig.WaitsLongAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_restored_connection_with_the_subscription_up_gets_the_notified_interval_again()
    {
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
        rig.Redis.OnConnectionFailed += Raise.Event();
        (await rig.WaitsLongAsync()).Should().BeFalse();
        rig.Subscriber.IsConnected(rig.Channel).Returns(true);

        rig.Redis.OnConnectionRestored += Raise.Event();

        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_restore_of_the_subscription_connection_that_failed_gets_the_notified_interval_again()
    {
        var first = new IPEndPoint(IPAddress.Loopback, 7000);
        var second = new IPEndPoint(IPAddress.Loopback, 7001);
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, first, ConnectionType.Subscription);
        (await rig.WaitsLongAsync()).Should().BeFalse();

        RaiseConnectionEvent(rig.Redis, failed: false, first, ConnectionType.Interactive);
        (await rig.WaitsLongAsync()).Should().BeFalse("a command connection coming back says nothing about the subscription");
        RaiseConnectionEvent(rig.Redis, failed: false, second, ConnectionType.Subscription);
        (await rig.WaitsLongAsync()).Should().BeFalse("another endpoint's subscription coming back says nothing about this one");

        RaiseConnectionEvent(rig.Redis, failed: false, first, ConnectionType.Subscription);
        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_restore_that_makes_the_doorbell_live_again_wakes_the_reader_at_once()
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, 7000);
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, endPoint, ConnectionType.Subscription);
        await rig.Waiter.WaitAsync(1.Milliseconds(), CancellationToken.None);

        RaiseConnectionEvent(rig.Redis, failed: false, endPoint, ConnectionType.Subscription);

        Func<Task> read = () => rig.Waiter.WaitAsync(CancellationToken.None);
        await read.Should().CompleteWithinAsync(2.Seconds(), "a ring sent before the subscription was taken again is lost, so the restore reads once");
    }

    [Fact]
    public async Task A_subscription_failure_before_a_reconnect_does_not_outlive_the_resubscribe()
    {
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription);

        rig.Redis.OnReconnected += Raise.Event();

        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_the_retired_connection_reports_during_the_resubscribe_does_not_keep_the_doorbell_down()
    {
        var retired = Substitute.For<IConnectionMultiplexer>();
        retired.TimeoutMilliseconds.Returns(5000);
        LiveRig? rig = null;
        var calls = 0;
        using var created = rig = new LiveRig(_fixture, subscribe: () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                RaiseConnectionEvent(rig!.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription, retired);
            }
            return Task.CompletedTask;
        });
        created.Subscriber.Multiplexer.Returns(retired);
        (await LiveRig.EventuallyAsync(created.WaitsLongAsync)).Should().BeTrue();
        var current = Substitute.For<IConnectionMultiplexer>();
        current.TimeoutMilliseconds.Returns(5000);
        created.Subscriber.Multiplexer.Returns(current);

        created.Redis.OnReconnected += Raise.Event();

        (await LiveRig.EventuallyAsync(async () => Volatile.Read(ref calls) >= 2 && await created.WaitsLongAsync())).Should().BeTrue("the retired connection never reports its restore");
    }

    [Fact]
    public async Task A_failed_command_connection_keeps_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();

        RaiseConnectionEvent(rig.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Interactive);

        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_subscription_failure_that_lands_while_the_subscribe_completes_keeps_the_poll_interval()
    {
        LiveRig? rig = null;
        var failed = 0;
        using var created = rig = new LiveRig(_fixture, subscribe: () =>
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                RaiseConnectionEvent(rig!.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription);
            }
            return Task.CompletedTask;
        });
        await LiveRig.EventuallyAsync(() => Task.FromResult(Volatile.Read(ref failed) == 1));
        await Task.Delay(200.Milliseconds(), TestContext.Current.CancellationToken);

        (await created.WaitsLongAsync()).Should().BeFalse("the subscription went down after the subscribe answered");
    }

    [Fact]
    public async Task A_forced_reconnect_keeps_the_poll_interval_until_the_subscription_is_taken_again()
    {
        var resubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var rig = new LiveRig(_fixture, subscribe: () => Interlocked.Increment(ref calls) == 1 ? Task.CompletedTask : resubscribe.Task);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();

        rig.Redis.OnReconnected += Raise.Event();

        (await LiveRig.EventuallyAsync(async () => !await rig.WaitsLongAsync())).Should().BeTrue();
        resubscribe.SetResult();
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();
    }

    [Fact]
    public async Task A_forced_reconnect_ends_the_notified_interval_before_the_resubscribe_is_due()
    {
        using var rig = new LiveRig(_fixture, dueTime: 1500.Milliseconds());
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();

        rig.Redis.OnReconnected += Raise.Event();

        (await rig.WaitsLongAsync()).Should().BeFalse("the subscription went with the old connection, and the new one is not taken for another 1.5 s");
    }

    [Fact]
    public async Task A_reconnect_racing_the_end_of_a_subscribe_never_leaves_that_subscription_live()
    {
        var notifyLive = typeof(SignalingFetchWaiter).GetField("_notifyLive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var subscribing = typeof(RedisStreamNotifyChannel).GetField("_subscribing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < 200; i++)
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource();
            var calls = 0;
            using var rig = new LiveRig(_fixture, dueTime: 10.Milliseconds(), subscribe: () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    return first.Task;
                }

                return never.Task;
            });
            await firstStarted.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() => { start.SignalAndWait(); first.SetResult(); }, TestContext.Current.CancellationToken),
                Task.Run(() => { start.SignalAndWait(); rig.Redis.OnReconnected += Raise.Event(); }, TestContext.Current.CancellationToken));
            (await LiveRig.EventuallyAsync(() => Task.FromResult(Volatile.Read(ref calls) >= 2 && (int)subscribing.GetValue(rig.Bell)! == 1))).Should().BeTrue();

            ((bool)notifyLive.GetValue(rig.Waiter)!).Should().BeFalse($"iteration {i}: the reconnect dropped the subscription, and its retry has not completed");
            never.SetException(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "done"));
        }
    }

    [Fact]
    public async Task A_reconnect_during_a_subscribe_does_not_make_that_subscription_live()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();
        var calls = 0;
        using var rig = new LiveRig(_fixture, dueTime: 1500.Milliseconds(), subscribe: () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                return first.Task;
            }

            return never.Task;
        });
        await firstStarted.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        rig.Redis.OnReconnected += Raise.Event();
        first.SetResult();

        (await rig.WaitsLongAsync()).Should().BeFalse("it may sit on the connection the reconnect replaced, so the retry decides");
    }

    [Fact]
    public async Task A_subscription_the_server_refuses_never_gets_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture, subscribe: () => throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM No permissions to access a channel"));
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);

        (await rig.WaitsLongAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_subscribe_keeps_the_poll_interval()
    {
        using var rig = new LiveRig(_fixture, subscribe: () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "boom"));
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);

        (await rig.WaitsLongAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_the_channel_ends_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture);
        (await LiveRig.EventuallyAsync(rig.WaitsLongAsync)).Should().BeTrue();

        rig.Bell.Dispose();

        (await rig.WaitsLongAsync()).Should().BeFalse();
    }

    private static void RaiseConnectionEvent(IRedisConnector redis, bool failed, EndPoint endPoint, ConnectionType connectionType, object? sender = null)
    {
        var args = (ConnectionFailedEventArgs)Activator.CreateInstance(
            typeof(ConnectionFailedEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            [null, null, endPoint, connectionType, ConnectionFailureType.SocketFailure, null, null],
            null)!;
        if (failed)
        {
            redis.OnConnectionFailed += Raise.Event<EventHandler>(sender ?? redis, args);
        }
        else
        {
            redis.OnConnectionRestored += Raise.Event<EventHandler>(sender ?? redis, args);
        }
    }

    /// <summary>A waiter with a 50 ms poll interval and a 30 s notified one, so a probe tells which applies.</summary>
    private sealed class LiveRig : IDisposable
    {
        public LiveRig(IFixture fixture, TimeSpan? dueTime = null, Func<Task>? subscribe = null)
        {
            Channel = RedisChannel.Literal("notify:test");
            Redis = Substitute.For<IRedisConnector>();
            Subscriber = Substitute.For<ISubscriber>();
            var multiplexer = Substitute.For<IConnectionMultiplexer>();
            multiplexer.TimeoutMilliseconds.Returns(5000);
            Subscriber.Multiplexer.Returns(multiplexer);
            Redis.Subscriber.Returns(Subscriber);
            if (subscribe is not null)
            {
                Subscriber.SubscribeAsync(Channel, Arg.Any<Action<RedisChannel, RedisValue>>()).Returns(_ => subscribe());
            }

            Waiter = new SignalingFetchWaiter(50.Milliseconds(), 30.Seconds());
            Bell = new RedisStreamNotifyChannel(Channel, Redis, fixture.Create<ILogger>(), Waiter, 50.Milliseconds(), dueTime ?? 10.Milliseconds(), "self");
        }

        public RedisChannel Channel { get; }

        public IRedisConnector Redis { get; }

        public ISubscriber Subscriber { get; }

        public SignalingFetchWaiter Waiter { get; }

        public RedisStreamNotifyChannel Bell { get; }

        public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
        {
            var until = DateTime.UtcNow + 10.Seconds();
            while (DateTime.UtcNow < until)
            {
                if (await condition())
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when a wait outlasts 400 ms, so the notified interval is in force; a pending wake is drained first.</summary>
        public async Task<bool> WaitsLongAsync()
        {
            await Waiter.WaitAsync(1.Milliseconds(), CancellationToken.None);
            var wait = Waiter.WaitAsync(CancellationToken.None);
            var long_ = await Task.WhenAny(wait, Task.Delay(400.Milliseconds(), TestContext.Current.CancellationToken)) != wait;
            Waiter.Signal();
            await wait;
            return long_;
        }

        public void Dispose()
        {
            Bell.Dispose();
            Waiter.Dispose();
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _records = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Records => [.. _records];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _records.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
