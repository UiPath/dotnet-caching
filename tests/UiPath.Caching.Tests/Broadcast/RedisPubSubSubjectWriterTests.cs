using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisPubSubSubjectWriterTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly TimeSpan _delay = 50.Milliseconds();
    private readonly TaskCompletionSource _subscribeCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ISubscriber _subscriber = default!;
    private Channel<ICacheEvent> _channel = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private RedisChannel _redisChannel = default!;
    private RedisPubSubTopicOptions _options = default!;
    private Action<RedisChannel, RedisValue>? _capturedAction;

    private RedisPubSubSubjectWriter<ICacheEvent>? _sut = null;

    [Fact]
    public async Task Receive_redis_null()
    {
        Sut();
        var action = await WaitForSubscribeAsync();
        action(_redisChannel, RedisValue.Null);
        await Task.Delay(_delay.Multiply(5), testContextAccessor.Current.CancellationToken);
        _channel.Reader.TryRead(out var item).Should().BeFalse();
    }

    [Fact]
    public async Task Receive_no_json()
    {
        Sut();
        var action = await WaitForSubscribeAsync();
        action(_redisChannel, (RedisValue)_fixture.Create<string>());
        await Task.Delay(_delay.Multiply(5), testContextAccessor.Current.CancellationToken);
        _channel.Reader.TryRead(out var item).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_event(bool valid)
    {
        var expected = _fixture.Create<TestCacheEvent>();
        expected.Valid = valid;
        var str = JsonSerializer.Serialize(expected);
        var expectedString = Encoding.UTF8.GetBytes(str);

        Sut();
        var action = await WaitForSubscribeAsync();
        action(_redisChannel, (RedisValue)expectedString);
        await Task.Delay(_delay.Multiply(3), testContextAccessor.Current.CancellationToken);
        _channel.Reader.TryRead(out var item).Should().Be(valid);
    }

    [Fact]
    public async Task A_resubscribe_after_a_reconnect_invalidates_and_the_first_subscribe_does_not()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        var resubscribed = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref resubscribed));
        var subscribes = 0;
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ => Interlocked.Increment(ref subscribes));

        Sut();
        await WaitForSubscribeAsync();
        await Task.Delay(_delay.Multiply(3), testContextAccessor.Current.CancellationToken);
        Volatile.Read(ref resubscribed).Should().Be(0, "nothing was cached before the first subscription");

        redis.OnReconnected += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref resubscribed) == 0)
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        Volatile.Read(ref subscribes).Should().Be(2);
        Volatile.Read(ref resubscribed).Should().Be(1);
    }

    [Fact]
    public async Task An_observer_that_joined_before_the_first_subscription_is_expired_once_it_is_in_place()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        var gaps = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref gaps));
        _options.SubscriberDueTime = TimeSpan.FromMilliseconds(200);

        Sut().MarkSubscribed();
        await WaitForSubscribeAsync();
        await Task.Delay(_delay.Multiply(3), testContextAccessor.Current.CancellationToken);
        Sut().MarkSubscribed();
        await Task.Delay(_delay.Multiply(3), testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref gaps).Should().Be(1, "only the observer that joined before the subscription could have missed a publication");
    }

    [Fact]
    public async Task A_reconnect_during_a_subscribe_attempt_subscribes_again_on_the_new_connection()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        _fixture.Inject<Action>(() => { });
        using var inFirstSubscribe = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var subscribes = 0;
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(_ =>
            {
                if (Interlocked.Increment(ref subscribes) == 1)
                {
                    inFirstSubscribe.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(10));
                }
            });

        Sut();
        inFirstSubscribe.Wait(TimeSpan.FromSeconds(10), testContextAccessor.Current.CancellationToken).Should().BeTrue();
        redis.OnReconnected += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        releaseFirst.Set();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref subscribes) < 2)
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        Volatile.Read(ref subscribes).Should().BeGreaterThanOrEqualTo(2, "the attempt that began before the reconnect subscribed the retired connection");
    }

    [Fact]
    public async Task A_write_a_full_channel_refuses_is_reported_as_dropped()
    {
        var full = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        full.Writer.TryWrite(new TestCacheEvent()).Should().BeTrue();
        _fixture.Inject((ChannelWriter<ICacheEvent>)full.Writer);
        var dropped = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref dropped));
        var expected = _fixture.Create<TestCacheEvent>();
        expected.Valid = true;

        Sut();
        var action = await WaitForSubscribeAsync();
        action(_redisChannel, (RedisValue)Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expected)));

        Volatile.Read(ref dropped).Should().Be(1, "the invalidation the refused write carried is gone");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task A_restore_after_a_failure_reports_a_subscription_gap(bool failed, int expected)
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        var gaps = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref gaps));

        Sut();
        await WaitForSubscribeAsync();
        if (failed)
        {
            redis.OnConnectionFailed += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        }

        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        await Task.Delay(_delay.Multiply(4), testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref gaps).Should().Be(expected, "publications while the connection was down never arrived, and a second restore found no outage");
    }

    [Fact]
    public async Task Only_the_restore_of_the_last_subscription_connection_down_reports_a_gap()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        var gaps = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref gaps));
        var first = new DnsEndPoint("node-1", 6379);
        var second = new DnsEndPoint("node-2", 6379);
        Sut();
        await WaitForSubscribeAsync();

        RaiseConnectionEvent(redis, failed: true, first, ConnectionType.Subscription);
        RaiseConnectionEvent(redis, failed: true, second, ConnectionType.Subscription);
        RaiseConnectionEvent(redis, failed: true, first, ConnectionType.Interactive);
        RaiseConnectionEvent(redis, failed: false, first, ConnectionType.Interactive);
        RaiseConnectionEvent(redis, failed: false, first, ConnectionType.Subscription);
        await Task.Delay(_delay.Multiply(4), testContextAccessor.Current.CancellationToken);
        Volatile.Read(ref gaps).Should().Be(0, "the second node's subscription is still down");

        RaiseConnectionEvent(redis, failed: false, second, ConnectionType.Subscription);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref gaps) == 0)
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        Volatile.Read(ref gaps).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_of_the_command_connection_alone_reports_no_gap()
    {
        var redis = _fixture.Freeze<IRedisConnector>();
        redis.Subscriber.Returns(_subscriber);
        var gaps = 0;
        _fixture.Inject<Action>(() => Interlocked.Increment(ref gaps));
        var node = new DnsEndPoint("node-1", 6379);
        Sut();
        await WaitForSubscribeAsync();

        RaiseConnectionEvent(redis, failed: true, node, ConnectionType.Interactive);
        RaiseConnectionEvent(redis, failed: false, node, ConnectionType.Interactive);
        await Task.Delay(_delay.Multiply(4), testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref gaps).Should().Be(0, "publications arrive on the subscription connection");
    }

    [Fact]
    public void Dispose_works()
    {
        var sut = Sut();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_unsubscribe_exception()
    {
        _subscriber.When(x => x.Unsubscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
        .Throw<Exception>();
        var sut = Sut();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _redisChannel = RedisChannel.Literal(_fixture.Create<string>());
        _fixture.Inject(_redisChannel);
        _formatter = new CacheClearEventFormatterProxy();
        _channel = Channel.CreateUnbounded<ICacheEvent>();
        _subscriber = _fixture.Freeze<ISubscriber>();
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                _capturedAction = ctx.Arg<Action<RedisChannel, RedisValue>>();
                _subscribeCalled.TrySetResult();
            });
        _fixture.Inject(_formatter);
        _fixture.Inject((ChannelWriter<ICacheEvent>)_channel);
        _options = new RedisPubSubTopicOptions
        {
            SubscriberTimeout = _delay,
            SubscriberDueTime = TimeSpan.Zero,
        };
        _fixture.Inject(_options);
        return ValueTask.CompletedTask;
    }

    private static void RaiseConnectionEvent(IRedisConnector redis, bool failed, EndPoint endPoint, ConnectionType connectionType)
    {
        var args = (ConnectionFailedEventArgs)Activator.CreateInstance(
            typeof(ConnectionFailedEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            [null, null, endPoint, connectionType, ConnectionFailureType.SocketFailure, null, null],
            null)!;
        if (failed)
        {
            redis.OnConnectionFailed += NSubstitute.Raise.Event<EventHandler>(redis, args);
        }
        else
        {
            redis.OnConnectionRestored += NSubstitute.Raise.Event<EventHandler>(redis, args);
        }
    }

    private RedisPubSubSubjectWriter<ICacheEvent> Sut() =>
        _sut ??= _fixture.Create<RedisPubSubSubjectWriter<ICacheEvent>>();

    private Task<Action<RedisChannel, RedisValue>> WaitForSubscribeAsync() =>
        _subscribeCalled.Task
            .WaitAsync(TimeSpan.FromSeconds(30), testContextAccessor.Current.CancellationToken)
            .ContinueWith(_ => _capturedAction!, TaskContinuationOptions.OnlyOnRanToCompletion);
}
