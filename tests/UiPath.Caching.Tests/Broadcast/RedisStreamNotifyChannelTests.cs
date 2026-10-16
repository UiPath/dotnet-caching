#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
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
