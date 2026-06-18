using FluentAssertions.Extensions;
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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ci => captured.TrySetResult(ci.Arg<Action<RedisChannel, RedisValue>>()));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());

        var capturedHandler = await captured.Task.WaitAsync(5.Seconds(), token);

        var waitTask = waiter.WaitAsync(token);
        capturedHandler(channel, RedisValue.EmptyString);

        var completed = await Task.WhenAny(waitTask, Task.Delay(2.Seconds(), token));
        completed.Should().BeSameAs(waitTask);
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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n >= 2)
                {
                    twoCalls.TrySetResult(true);
                }
                if (n == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "boom");
                }
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 50.Milliseconds(), 10.Milliseconds());
        await twoCalls.Task.WaitAsync(5.Seconds(), token);

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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());
        await subscribed.Task.WaitAsync(5.Seconds(), token);

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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(50.Milliseconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());
        await subscribed.Task.WaitAsync(5.Seconds(), token);

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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1) firstCall.TrySetResult(true);
                if (n >= 2) secondCall.TrySetResult(true);
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds());
        await firstCall.Task.WaitAsync(5.Seconds(), token);

        redis.OnReconnected += Raise.Event();

        await secondCall.Task.WaitAsync(5.Seconds(), token);
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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());
        await subscribed.Task.WaitAsync(5.Seconds(), token);

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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));
        subscriber.When(s => s.Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());
        await subscribed.Task.WaitAsync(5.Seconds(), token);

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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => subscribed.TrySetResult(true));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds());
        await subscribed.Task.WaitAsync(5.Seconds(), token);
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
        subscriber.When(s => s.Subscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                var n = Interlocked.Increment(ref subscribeCalls);
                if (n == 1) firstSubscribed.TrySetResult(true);
                if (n >= 2) secondSubscribed.TrySetResult(true);
            });
        subscriber.When(s => s.Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "stale"));

        using var waiter = new SignalingFetchWaiter(5.Seconds());
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds());
        await firstSubscribed.Task.WaitAsync(5.Seconds(), token);

        redis.OnReconnected += Raise.Event();

        await secondSubscribed.Task.WaitAsync(5.Seconds(), token);
        Volatile.Read(ref subscribeCalls).Should().BeGreaterThanOrEqualTo(2);
    }
}
