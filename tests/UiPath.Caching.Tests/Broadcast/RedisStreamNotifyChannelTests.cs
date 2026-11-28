#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamNotifyChannelTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly ManualTimeProvider _clock = NewClock();

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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        var capturedHandler = await captured.Task.WaitAsync(token);

        var waitTask = waiter.WaitAsync(token);
        capturedHandler(channel, RedisValue.EmptyString);

        await waitTask.WaitAsync(token);

        waitTask.IsCompletedSuccessfully.Should().BeTrue("the incoming message signals the waiter");
    }

    [Fact]
    public async Task Using_another_channel_unsubscribes_the_old_one_and_subscribes_the_new()
    {
        var token = TestContext.Current.CancellationToken;
        var sharded = RedisChannel.Sharded("notify{1}");
        var literal = RedisChannel.Literal("notify:1");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var subscribedLiteral = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribedSharded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(sharded, Arg.Any<Action<RedisChannel, RedisValue>>())).Do(_ => subscribedSharded.TrySetResult());
        subscriber.When(s => s.SubscribeAsync(literal, Arg.Any<Action<RedisChannel, RedisValue>>())).Do(_ => subscribedLiteral.TrySetResult());

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(sharded, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribedSharded.Task.WaitAsync(token);
        _clock.Advance(100.Milliseconds());

        sut.UseChannel(literal);
        _clock.Advance(10.Milliseconds());

        await subscribedLiteral.Task.WaitAsync(token);
        subscriber.Received(1).Unsubscribe(sharded, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_refusal_the_owner_handles_does_not_deny_the_subscription()
    {
        var token = TestContext.Current.CancellationToken;
        var sharded = RedisChannel.Sharded("notify{1}");
        var literal = RedisChannel.Literal("notify:1");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        subscriber.SubscribeAsync(sharded, Arg.Any<Action<RedisChannel, RedisValue>>())
            .Returns(Task.FromException(StreamsFakes.UnknownCommand()));
        var subscribedLiteral = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(s => s.SubscribeAsync(literal, Arg.Any<Action<RedisChannel, RedisValue>>())).Do(_ => subscribedLiteral.TrySetResult());

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        RedisStreamNotifyChannel? sut = null;
        sut = new RedisStreamNotifyChannel(
            sharded,
            redis,
            _fixture.Create<ILogger>(),
            waiter,
            null,
            10.Milliseconds(),
            "self",
            _ =>
        {
            sut!.UseChannel(literal);
            return true;
        },
            clock: _clock);
        using var owned = sut;
        _clock.Advance(10.Milliseconds());
        _clock.Advance(10.Milliseconds());

        await subscribedLiteral.Task.WaitAsync(token);
        waiter.NotifyLive.Should().BeTrue();
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        var capturedHandler = await captured.Task.WaitAsync(token);
        // The subscribe itself signals once; drain that before ringing.
        await waiter.WaitAsync(token);

        var waitTask = waiter.WaitAsync(token);
        capturedHandler(channel, "self");
        waitTask.IsCompleted.Should().BeFalse( "this node's own entries are never dispatched, so its own ring is not worth a read");

        capturedHandler(channel, "other");
        await waitTask.WaitAsync(token);
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 50.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        _clock.Advance(50.Milliseconds());
        await twoCalls.Task.WaitAsync(token);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_disposed_connector_ends_the_subscribe_retries()
    {
        var token = TestContext.Current.CancellationToken;
        var redis = _fixture.Freeze<IRedisConnector>();
        var calls = 0;
        var firstCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            firstCall.TrySetResult(true);
            return ValueTask.FromException(new ObjectDisposedException(nameof(IRedisConnector)));
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(RedisChannel.Literal("notify:test"), redis, _fixture.Create<ILogger>(), waiter, 20.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        await firstCall.Task.WaitAsync(token);
        _clock.Advance(200.Milliseconds());
        var settled = Volatile.Read(ref calls);
        _clock.Advance(300.Milliseconds());

        settled.Should().Be(1);
        Volatile.Read(ref calls).Should().Be(settled, "a connector that is gone for good is not tried again");
    }

    [Fact]
    public void A_stale_subscribe_tick_does_not_reconnect_a_disposed_connector()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        var calls = 0;
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromException(new ObjectDisposedException(nameof(IRedisConnector)));
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(RedisChannel.Literal("notify:test"), redis, _fixture.Create<ILogger>(), waiter, 1.Hours(), 1.Hours(), "self", clock: _clock);
        var subscribe = typeof(RedisStreamNotifyChannel).GetMethod("Subscribe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // The failed connect completes synchronously, so the first tick is done when Invoke returns.
        subscribe.Invoke(sut, [null]);
        subscribe.Invoke(sut, [null]);

        Volatile.Read(ref calls).Should().Be(1, "a tick queued before the connector was seen gone must not connect again");
    }

    [Fact]
    public async Task A_tick_past_the_entry_guard_when_the_connector_goes_gone_does_not_connect()
    {
        var token = TestContext.Current.CancellationToken;
        var redis = _fixture.Freeze<IRedisConnector>();
        var calls = 0;
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromException(new ObjectDisposedException(nameof(IRedisConnector)));
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(RedisChannel.Literal("notify:test"), redis, _fixture.Create<ILogger>(), waiter, 1.Hours(), 1.Hours(), "self", clock: _clock);
        var subscribe = typeof(RedisStreamNotifyChannel).GetMethod("Subscribe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        sut.AfterEntryGuardForTests = () =>
        {
            sut.AfterEntryGuardForTests = null;
            entered.Set();
            gate.Wait(token);
        };

        var tickB = Task.Run(() => subscribe.Invoke(sut, [null]), token);
        entered.Wait(token);
        // Tick A runs the disposed-connector path to the end while B holds the stale view of the flags.
        subscribe.Invoke(sut, [null]);
        gate.Set();
        await tickB.WaitAsync(token);

        Volatile.Read(ref calls).Should().Be(1, "the re-check after the interlock stops a tick that passed the guard before the flag was set");
    }

    [Fact]
    public async Task A_reconnect_rearms_the_timer_after_the_subscribe_step_hit_a_retired_multiplexer()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var attempts = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                first.TrySetResult();
                return Task.FromException(new ObjectDisposedException(nameof(IConnectionMultiplexer)));
            }
            second.TrySetResult();
            return Task.CompletedTask;
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 1.Hours(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        await first.Task.WaitAsync(token);
        redis.OnReconnected += Raise.Event();
        _clock.Advance(10.Milliseconds());

        await second.Task.WaitAsync(token);
        second.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void A_retired_multiplexer_failing_the_subscribe_step_does_not_end_the_resubscribes()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();
        var subscriber = _fixture.Freeze<ISubscriber>();
        var multiplexer = _fixture.Freeze<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(5000);
        subscriber.Multiplexer.Returns(multiplexer);
        redis.Subscriber.Returns(subscriber);
        var attempts = 0;
        subscriber.SubscribeAsync(channel, Arg.Any<Action<RedisChannel, RedisValue>>()).Returns(_ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new ObjectDisposedException(nameof(IConnectionMultiplexer)))
                : Task.CompletedTask);

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 1.Hours(), 1.Hours(), "self", clock: _clock);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var subscribe = typeof(RedisStreamNotifyChannel).GetMethod("Subscribe", flags)!;

        subscribe.Invoke(sut, [null]);
        redis.OnReconnected += Raise.Event();
        subscribe.Invoke(sut, [null]);

        Volatile.Read(ref attempts).Should().Be(2, "an ObjectDisposedException past the connect is not the connector being gone");
    }

    [Fact]
    public async Task A_transient_connect_failure_is_retried_while_alive()
    {
        var token = TestContext.Current.CancellationToken;
        var redis = _fixture.Freeze<IRedisConnector>();
        var calls = 0;
        var threeCalls = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 3)
            {
                threeCalls.TrySetResult(true);
            }
            return ValueTask.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down"));
        });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(RedisChannel.Literal("notify:test"), redis, _fixture.Create<ILogger>(), waiter, 20.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        _clock.Advance(20.Milliseconds());
        _clock.Advance(20.Milliseconds());
        await threeCalls.Task.WaitAsync(token);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(3, "a transient failure is retried while the channel is alive");
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribed.Task.WaitAsync(token);

        sut.Dispose();

#pragma warning disable S6966 // The code under test calls the sync Unsubscribe, so that is what is verified.
        subscriber.Received().Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
#pragma warning restore S6966
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

        using var waiter = new SignalingFetchWaiter(50.Milliseconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribed.Task.WaitAsync(token);

        sut.Dispose();

        var wait = waiter.WaitAsync(token);
        _clock.Advance(50.Milliseconds());
        Func<Task> act = () => wait;
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await firstCall.Task.WaitAsync(token);

        redis.OnReconnected += Raise.Event();
        _clock.Advance(10.Milliseconds());

        _clock.Advance(10.Milliseconds());
        await secondCall.Task.WaitAsync(token);
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
        _clock.Advance(10.Milliseconds());
                }
                if (n >= 2)
                {
                    secondCall.TrySetResult(true);
                }
            });

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        _clock.Advance(10.Milliseconds());
        await secondCall.Task.WaitAsync(token);
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribed.Task.WaitAsync(token);

        sut.Dispose();
        Action act = () => sut.Dispose();

        act.Should().NotThrow();
#pragma warning disable S6966 // The code under test calls the sync Unsubscribe, so that is what is verified.
        subscriber.Received(1).Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
#pragma warning restore S6966
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribed.Task.WaitAsync(token);

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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await subscribed.Task.WaitAsync(token);
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await firstSubscribed.Task.WaitAsync(token);

        redis.OnReconnected += Raise.Event();
        _clock.Advance(10.Milliseconds());

        await secondSubscribed.Task.WaitAsync(token);
        Volatile.Read(ref subscribeCalls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData("NOPERM No permissions to access a channel")]
    [InlineData("ERR unknown command 'SUBSCRIBE'")]
    public void A_subscription_the_server_refuses_is_warned_about_once_and_not_tried_again(string error)
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, logger, waiter, 50.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        logger.Records.Any(r => r.Message.Contains("polls only")).Should().BeTrue();
        redis.OnReconnected += Raise.Event();
        _clock.Advance(10.Milliseconds());
        _clock.Advance(300.Milliseconds());

        Volatile.Read(ref calls).Should().Be(1, "the server will refuse it again; this topic polls");
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("polls only"));
        logger.Records.Should().NotContain(r => r.Level == LogLevel.Error);
    }

    [Fact]
    public void The_constructor_does_not_touch_the_connection()
    {
        var channel = RedisChannel.Literal("notify:test");
        var redis = _fixture.Freeze<IRedisConnector>();

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, null, 60.Seconds(), "self", clock: _clock);

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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 50.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());

        _clock.Advance(50.Milliseconds());
        await subscribed.Task.WaitAsync(token);
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

        var unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.When(x => x.Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())).Do(_ => unsubscribed.TrySetResult());
        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        var sut = new RedisStreamNotifyChannel(channel, redis, _fixture.Create<ILogger>(), waiter, 60.Seconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        await started.Task.WaitAsync(token);

        var timerCount = _clock.TimersCreated;
        var disposing = Task.Run(sut.Dispose, token);
        await _clock.WaitForTimersAsync(timerCount + 1);
        _clock.Advance(5.Seconds());
        await disposing;
        pending.SetResult();

        await unsubscribed.Task.WaitAsync(token);

#pragma warning disable S6966 // The code under test calls the sync Unsubscribe, so that is what is verified.
        subscriber.Received(1).Unsubscribe(channel, Arg.Any<Action<RedisChannel, RedisValue>>(), CommandFlags.FireAndForget);
#pragma warning restore S6966
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

        using var waiter = new SignalingFetchWaiter(5.Seconds(), timeProvider: _clock);
        using var sut = new RedisStreamNotifyChannel(channel, redis, logger, waiter, 50.Milliseconds(), 10.Milliseconds(), "self", clock: _clock);
        _clock.Advance(10.Milliseconds());
        _clock.Advance(50.Milliseconds());
        _clock.Advance(50.Milliseconds());
        await subscribed.Task.WaitAsync(token);

        logger.Records.Where(r => r.Message.Contains("subscribe error")).Select(r => r.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
        logger.Records.Should().NotContain(r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Until_the_subscription_is_up_the_poll_interval_applies()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock(), dueTime: 1.Hours());

        (await rig.PollsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Once_subscribed_the_notified_interval_applies()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());

        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_connection_ends_the_long_wait_in_progress_and_the_poll_interval_applies()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        await rig.DrainAsync();
        var wait = rig.Waiter.WaitAsync(CancellationToken.None);
        rig.Clock!.Advance(1.Seconds());
        wait.IsCompleted.Should().BeFalse();

        rig.Redis.OnConnectionFailed += Raise.Event();

        await wait.WaitAsync(TestContext.Current.CancellationToken);
        (await rig.PollsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_restored_connection_with_the_subscription_up_gets_the_notified_interval_again()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        rig.Redis.OnConnectionFailed += Raise.Event();
        (await rig.PollsAsync()).Should().BeTrue();
        rig.Subscriber.IsConnected(rig.Channel).Returns(true);

        rig.Redis.OnConnectionRestored += Raise.Event();

        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_restore_of_the_subscription_connection_that_failed_gets_the_notified_interval_again()
    {
        var first = new IPEndPoint(IPAddress.Loopback, 7000);
        var second = new IPEndPoint(IPAddress.Loopback, 7001);
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, first, ConnectionType.Subscription);
        (await rig.PollsAsync()).Should().BeTrue();

        RaiseConnectionEvent(rig.Redis, failed: false, first, ConnectionType.Interactive);
        (await rig.PollsAsync()).Should().BeTrue("a command connection coming back says nothing about the subscription");
        RaiseConnectionEvent(rig.Redis, failed: false, second, ConnectionType.Subscription);
        (await rig.PollsAsync()).Should().BeTrue("another endpoint's subscription coming back says nothing about this one");

        RaiseConnectionEvent(rig.Redis, failed: false, first, ConnectionType.Subscription);
        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_restore_that_makes_the_doorbell_live_again_wakes_the_reader_at_once()
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, 7000);
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, endPoint, ConnectionType.Subscription);
        await rig.DrainAsync();

        RaiseConnectionEvent(rig.Redis, failed: false, endPoint, ConnectionType.Subscription);

        await rig.Waiter.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_subscription_failure_before_a_reconnect_does_not_outlive_the_resubscribe()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        RaiseConnectionEvent(rig.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription);

        rig.Redis.OnReconnected += Raise.Event();

        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_the_retired_connection_reports_during_the_resubscribe_does_not_keep_the_doorbell_down()
    {
        var retired = Substitute.For<IConnectionMultiplexer>();
        retired.TimeoutMilliseconds.Returns(5000);
        LiveRig? rig = null;
        var reconnecting = 0;
        var resubscribes = 0;
        using var created = rig = new LiveRig(_fixture, clock: NewClock(), subscribe: () =>
        {
            // Every subscribe from the reconnect on, not a call count: a timer tick can add a subscribe before it.
            if (Volatile.Read(ref reconnecting) == 1)
            {
                Interlocked.Increment(ref resubscribes);
                RaiseConnectionEvent(rig!.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription, retired);
            }
            return Task.CompletedTask;
        });
        created.Subscriber.Multiplexer.Returns(retired);
        created.Subscribe();
        created.SubscriptionFinished();
        (await created.WaitsLongAsync()).Should().BeTrue();
        var current = Substitute.For<IConnectionMultiplexer>();
        current.TimeoutMilliseconds.Returns(5000);
        created.Subscriber.Multiplexer.Returns(current);

        Volatile.Write(ref reconnecting, 1);
        created.Redis.OnReconnected += Raise.Event();

        created.Subscribe();
        created.SubscriptionFinished();
        (Volatile.Read(ref resubscribes) >= 1 && await created.WaitsLongAsync()).Should().BeTrue("the retired connection never reports its restore");
    }

    [Fact]
    public async Task A_subscribe_on_the_new_connection_before_the_reconnect_event_still_counts_its_failures()
    {
        var current = Substitute.For<IConnectionMultiplexer>();
        current.TimeoutMilliseconds.Returns(5000);
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscriber.Multiplexer.Returns(current);
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
        rig.Redis.OnReconnected += Raise.Event();
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();

        RaiseConnectionEvent(rig.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription, current);

        (await rig.PollsAsync()).Should().BeTrue("the connector swapped to this connection before the event, so it is the current one");
    }

    [Fact]
    public async Task A_failed_command_connection_keeps_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();

        RaiseConnectionEvent(rig.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Interactive);

        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_subscription_failure_that_lands_while_the_subscribe_completes_keeps_the_poll_interval()
    {
        LiveRig? rig = null;
        var failed = 0;
        var logger = new RecordingLogger();
        using var created = rig = new LiveRig(_fixture, clock: NewClock(), logger: logger, subscribe: () =>
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                RaiseConnectionEvent(rig!.Redis, failed: true, new IPEndPoint(IPAddress.Loopback, 7000), ConnectionType.Subscription);
                return Task.CompletedTask;
            }

            // The subscription connection is still down: a timer tick queued before the subscribe answered retries, and fails.
            throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down");
        });
        created.Subscribe();
        created.SubscriptionFinished();
        logger.Records.Any(r => r.Message.StartsWith("Stream notify subscribed", StringComparison.Ordinal)).Should().BeTrue();

        (await created.PollsAsync()).Should().BeTrue("the subscription went down after the subscribe answered");
    }

    [Fact]
    public async Task A_forced_reconnect_keeps_the_poll_interval_until_the_subscription_is_taken_again()
    {
        var resubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var rig = new LiveRig(_fixture, clock: NewClock(), subscribe: () => Interlocked.Increment(ref calls) == 1 ? Task.CompletedTask : resubscribe.Task);
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();

        rig.Redis.OnReconnected += Raise.Event();

        rig.Subscribe();
        (await rig.PollsAsync()).Should().BeTrue();
        resubscribe.SetResult();
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_forced_reconnect_ends_the_notified_interval_before_the_resubscribe_is_due()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock(), dueTime: 1500.Milliseconds());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();

        rig.Redis.OnReconnected += Raise.Event();

        (await rig.PollsAsync()).Should().BeTrue("the subscription went with the old connection, and the new one is not taken for another 1.5 s");
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
            rig.Subscribe();
            await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() => { start.SignalAndWait(); first.SetResult(); }, TestContext.Current.CancellationToken),
                Task.Run(() => { start.SignalAndWait(); rig.Redis.OnReconnected += Raise.Event(); }, TestContext.Current.CancellationToken));
            rig.SubscriptionFinished();
            rig.Subscribe();
            (Volatile.Read(ref calls) >= 2 && (int)subscribing.GetValue(rig.Bell)! == 1).Should().BeTrue();

            ((bool)notifyLive.GetValue(rig.Waiter)!).Should().BeFalse($"iteration {i}: the reconnect dropped the subscription, and its retry has not completed");
            never.SetException(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "done"));
            rig.SubscriptionFinished();
        }
    }

    [Fact]
    public async Task A_reconnect_during_a_subscribe_does_not_make_that_subscription_live()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();
        var calls = 0;
        using var rig = new LiveRig(_fixture, clock: NewClock(), dueTime: 1500.Milliseconds(), subscribe: () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                return first.Task;
            }

            return never.Task;
        });
        rig.Subscribe();
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        rig.Redis.OnReconnected += Raise.Event();
        first.SetResult();
        rig.SubscriptionFinished();

        (await rig.PollsAsync()).Should().BeTrue("it may sit on the connection the reconnect replaced, so the retry decides");
    }

    [Fact]
    public async Task A_subscription_the_server_refuses_never_gets_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock(), subscribe: () => throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM No permissions to access a channel"));
        rig.Clock.Advance(300.Milliseconds());

        (await rig.PollsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_subscribe_keeps_the_poll_interval()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock(), subscribe: () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "boom"));
        rig.Clock.Advance(300.Milliseconds());

        (await rig.PollsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_the_channel_ends_the_notified_interval()
    {
        using var rig = new LiveRig(_fixture, clock: NewClock());
        rig.Subscribe();
        rig.SubscriptionFinished();
        (await rig.WaitsLongAsync()).Should().BeTrue();

        rig.Bell.Dispose();

        (await rig.PollsAsync()).Should().BeTrue();
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

    private static ManualTimeProvider NewClock() => new(DateTimeOffset.UnixEpoch);

    /// <summary>A waiter with a 50 ms poll interval and a 30 s notified one, so a probe tells which applies.</summary>
    private sealed class LiveRig : IDisposable
    {
        public LiveRig(IFixture fixture, TimeSpan? dueTime = null, Func<Task>? subscribe = null, ILogger? logger = null, TimeProvider? clock = null)
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

            Clock = clock as ManualTimeProvider ?? NewClock();
            DueTime = dueTime ?? 10.Milliseconds();
            Waiter = new SignalingFetchWaiter(50.Milliseconds(), 30.Seconds(), Clock);
            Bell = new RedisStreamNotifyChannel(Channel, Redis, logger ?? fixture.Create<ILogger>(), Waiter, 50.Milliseconds(), DueTime, "self", clock: Clock);
        }

        public RedisChannel Channel { get; }

        public IRedisConnector Redis { get; }

        public ISubscriber Subscriber { get; }

        public SignalingFetchWaiter Waiter { get; }

        /// <summary>The manual clock the waiter runs on; the timing helpers need the rig built with one.</summary>
        public ManualTimeProvider Clock { get; }

        public TimeSpan DueTime { get; }

        public RedisStreamNotifyChannel Bell { get; }

        public void Subscribe()
        {
            Clock.Advance(DueTime);
        }

        public void SubscriptionFinished() =>
            ((ManualResetEventSlim)typeof(RedisStreamNotifyChannel).GetField("_subscribingDone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Bell)!).Wait(TestContext.Current.CancellationToken);

        /// <summary>Consumes a pending wake, if any, by running a 1 ms wait past its timeout.</summary>
        public async Task DrainAsync()
        {
            var drain = Waiter.WaitAsync(1.Milliseconds(), CancellationToken.None);
            Clock!.Advance(1.Milliseconds());
            await drain.WaitAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>True when a wait is still pending after the clock moves past the poll interval but not the notified one, so the notified interval is in force; a pending wake is drained first. Only a starved test thread can make a poll look long, and a test that expects the poll interval asks <see cref="PollsAsync"/>.</summary>
        public async Task<bool> WaitsLongAsync()
        {
            await DrainAsync();
            var wait = Waiter.WaitAsync(CancellationToken.None);
            Clock!.Advance(1.Seconds());
            var long_ = !wait.IsCompleted && Waiter.NotifyLive;
            Waiter.Signal();
            await wait;
            return long_;
        }

        /// <summary>True when a wait ends once the clock moves past the poll interval but not the notified one, so the poll interval is in force; a pending wake is drained first.</summary>
        public async Task<bool> PollsAsync()
        {
            await DrainAsync();
            var wait = Waiter.WaitAsync(CancellationToken.None);
            Clock!.Advance(1.Seconds());
            await wait.WaitAsync(TestContext.Current.CancellationToken);
            return true;
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
