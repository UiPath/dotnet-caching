using System.Text;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisPubSubTopicTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly List<ICacheEvent> _onNextMessages = [];
    private readonly TimeSpan _delay = 50.Milliseconds();
    private readonly TaskCompletionSource _subscribeCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unsubscribeCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TopicKey _topicKey;
    private ISubscriber _subscriber = default!;
    private IObserver<ICacheEvent> _observer = default!;
    private bool _onCompleted = false;
    private Action<RedisChannel, RedisValue>? _handler;
    private TestCacheEventFormatterProxy _formatter = default!;
    private IDatabase _database = default!;
    private IRedisConnector _redisConnector = default!;
    private string _channel = default!;
    private string _value = default!;
    private IRedisChannelStrategy _redisChannelStrategy = default!;
    private IResiliencePipelineProvider _resiliencePipelineProvider = default!;
    private RedisPubSubTopicOptions _options = default!;
    private string? _actualRedisChannel;
    private bool _isConnected = true;

    private RedisPubSubTopic<ICacheEvent>? _sut;

    [Fact]
    public async Task Publish_WhenDisconnected()
    {
        _isConnected = false;
        var sut = await Sut();
        var actual = await sut.PublishAsync(new TestCacheEvent(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
        await _database.DidNotReceive().PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task It_Subscribes_to_subscriber()
    {
        var sut = await Sut();
        var disposable = sut.Subscribe(_observer);
        disposable.Should().NotBeNull();
        await WaitForSubscribeAsync();
        _subscriber.Received().Subscribe(Arg.Is<RedisChannel>(rc => rc.ToString().EndsWith((string)_topicKey)), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Message_received_when_channel_event_received()
    {
        var sut = await Sut();
        var disposable = sut.Subscribe(_observer);
        var machineName = _fixture.Create<string>();

        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri($"urn:{machineName}"),
        };
        var bytes = _formatter.Encode(cloudEvent);
        var message = Encoding.UTF8.GetString(bytes.Span);

        for (var count = 0; _handler is null && count < 100; count++)
        {
            await Task.Delay(50.Milliseconds(), testContextAccessor.Current.CancellationToken);
        }
        _handler.Should().NotBeNull("the topic must subscribe and capture the message handler");

        _handler!.Invoke(RedisChannel.Literal(_topicKey.Name), message);
        for (var count = 0; _onNextMessages.Count == 0 && count < 100; count++)
        {
            await Task.Delay(100.Milliseconds(), testContextAccessor.Current.CancellationToken);
        }
        _onNextMessages.FirstOrDefault().Should().BeEquivalentTo(cloudEvent);
        _onCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task No_message_when_subscription_disposed()
    {
        var sut = await Sut();
        var disposable = sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        sut.Dispose();
        _handler?.Invoke(RedisChannel.Literal(_topicKey.Name), message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task No_message_when_channel_is_unknown()
    {
        var sut = await Sut();
        var disposable = sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        RedisChannel newChannel = RedisChannel.Literal(_fixture.Create<string>());

        _handler?.Invoke(newChannel, message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeFalse();
    }


    [Fact]
    public async Task Exception_is_not_thrown_when_subscribe_fails()
    {
        var sut = await Sut();
        TopicKey channel = _fixture.Create<string>();
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new Exception());

        Action act = () => sut.Subscribe(_fixture.Create<IObserver<ICacheEvent>>());
        act.Should().NotThrow<Exception>();
    }

    [Fact]
    public async Task Subscriber_is_called_for_specific_redis_channel()
    {
        var sut = await Sut();
        var disposable = sut.Subscribe(_observer);
        await WaitForSubscribeAsync();
        _subscriber.Received().Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        await _subscriber.DidNotReceive().SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        disposable.Should().NotBeNull();
        _topicKey.Name.Should().BeEquivalentTo(_actualRedisChannel);
    }

    [Fact]
    public async Task Unsubscribe_is_called_when_subscriber_is_disposed()
    {
        var sut = await Sut();
        await WaitForSubscribeAsync();
        sut.Dispose();
        await _unsubscribeCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), testContextAccessor.Current.CancellationToken);
        _subscriber.Received().Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>?>(), Arg.Any<CommandFlags>());
        _topicKey.Name.Should().BeEquivalentTo(_actualRedisChannel);
    }
    [Fact]
    public async Task Publish_works_as_expected()
    {
        var sut = await Sut();
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
        };
        _database.ClearReceivedCalls();
        var executed = false;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(c =>
            {
                executed = true;
                return _fixture.Create<long>();
            });
        await sut.PublishAsync(cloudEvent, testContextAccessor.Current.CancellationToken);
        executed.Should().BeTrue();
    }


    [Fact]
    public async Task Canceling_token_stops_execution()
    {
        var sut = await Sut();
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
        };
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> act = async () => await sut.PublishAsync(cloudEvent, token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task No_exceptions_are_thrown_when_redis_fails()
    {
        var sut = await Sut();
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
        };
        _database.ClearReceivedCalls();
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        Func<Task> act = async () => await sut.PublishAsync(cloudEvent);
        await act.Should().NotThrowAsync();
        _channel.Should().BeNull();
        _value.Should().BeNull();
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_observer_joining_while_the_first_subscription_goes_in_place_is_expired(bool throws)
    {
        var subject = new HeldSubject();
        _fixture.Inject<Func<IEventSubject<ICacheEvent>>>(() => subject);
        _options.SubscriberDueTime = TimeSpan.FromSeconds(1);
        var sut = await Sut();
        var entry = new KeptEntry { Throws = throws };

        var joining = Task.Run(() => sut.Subscribe(entry), testContextAccessor.Current.CancellationToken);
        subject.Joining.Wait(TimeSpan.FromSeconds(10), testContextAccessor.Current.CancellationToken).Should().BeTrue();
        await WaitForSubscribeAsync();
        await Task.Delay(_delay.Multiply(2), testContextAccessor.Current.CancellationToken);
        subject.Release.Set();
        (await joining).Dispose();

        entry.Missed.Task.IsCompleted.Should().BeTrue("the subscription went in place after the mark but before this observer joined");
    }

    [Fact]
    public async Task A_restored_connection_expires_what_the_topic_keeps()
    {
        var sut = await Sut();
        await WaitForSubscribeAsync();
        var entry = new KeptEntry();
        using var subscription = sut.Subscribe(entry);

        _redisConnector.OnConnectionFailed += Raise.Event<EventHandler>(_redisConnector, EventArgs.Empty);
        _redisConnector.OnConnectionRestored += Raise.Event<EventHandler>(_redisConnector, EventArgs.Empty);

        await entry.Missed.Awaiting(m => m.Task).Should().CompleteWithinAsync(TimeSpan.FromSeconds(10), "the topic expires its entries itself, on whatever local tier holds them");
    }

    [Fact]
    public async Task A_resubscribe_after_a_reconnect_expires_what_the_topic_keeps()
    {
        var sut = await Sut();
        await WaitForSubscribeAsync();
        var entry = new KeptEntry();
        using var subscription = sut.Subscribe(entry);

        _redisConnector.OnReconnected += Raise.Event<EventHandler>(_redisConnector, EventArgs.Empty);

        await entry.Missed.Awaiting(m => m.Task).Should().CompleteWithinAsync(TimeSpan.FromSeconds(10), "publications between the swap and the new subscription never arrived");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _database = _fixture.Freeze<IDatabase>();

        _fixture.Inject<Func<IDatabase>>(() => _database);
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(c =>
            {
                _channel = c.Arg<RedisChannel>()!;
                return 1;
            });
        _topicKey = _fixture.Freeze<string>();
        _fixture.Inject(_topicKey);
        _fixture.Inject<Func<IEventSubject<ICacheEvent>>>(() => new KeyedSubject<ICacheEvent>(Microsoft.Extensions.Logging.Abstractions.NullLogger<KeyedSubject<ICacheEvent>>.Instance));
        _redisChannelStrategy = _fixture.Freeze<IRedisChannelStrategy>();
        _redisChannelStrategy.GetRedisChannel(_topicKey).Returns(c => new RedisChannel(_topicKey, RedisChannel.PatternMode.Auto));
        _resiliencePipelineProvider = EmptyResiliencePipelineProvider.Instance;
        _fixture.Inject(_resiliencePipelineProvider);
        _subscriber = _fixture.Freeze<ISubscriber>();
        _observer = _fixture.Freeze<IObserver<ICacheEvent>>();
        _observer.When(x => x.OnNext(Arg.Any<ICacheEvent>()))
            .Do(c => _onNextMessages.Add(c.Arg<ICacheEvent>()!));
        _observer.When(x => x.OnCompleted())
            .Do(c => _onCompleted = true);
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c =>
            {
                _actualRedisChannel = c.Arg<RedisChannel>().ToString();
                _handler = c.Arg<Action<RedisChannel, RedisValue>>();
                _subscribeCalled.TrySetResult();
            });
        _subscriber.When(x => x.Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>?>(), Arg.Any<CommandFlags>()))
            .Do(c =>
            {
                _actualRedisChannel = c.Arg<RedisChannel>().ToString();
                _unsubscribeCalled.TrySetResult();
            });
        _formatter = new TestCacheEventFormatterProxy();
        _fixture.Inject<IEventFormatterProxy<ICacheEvent>>(_formatter);
        _options = new RedisPubSubTopicOptions
        {
            SubscriberTimeout = _delay,
            SubscriberDueTime = TimeSpan.Zero,
            ConnectionMonitorEnabled = true,
        };
        _fixture.Inject(_options);

        _redisConnector = _fixture.Freeze<IRedisConnector>();
        _redisConnector.Database.Returns(_database);
        _redisConnector.Subscriber.Returns(_subscriber);
        _redisConnector.IsConnected.Returns(ctx => _isConnected);

        _fixture.Inject<IConnectionState>(_redisConnector);

        return ValueTask.CompletedTask;
    }
    private async Task<RedisPubSubTopic<ICacheEvent>> Sut(int delayMultiplier = 2)
    {
        if (_sut != null)
        {
            return _sut;
        }
        _sut = _fixture.Create<RedisPubSubTopic<ICacheEvent>>();
        await Task.Delay(_delay.Multiply(delayMultiplier), testContextAccessor.Current.CancellationToken);
        return _sut;
    }

    private Task WaitForSubscribeAsync() =>
        _subscribeCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), testContextAccessor.Current.CancellationToken);

    /// <summary>A subject whose next subscription waits, mid-join, until released.</summary>
    private sealed class HeldSubject : IEventSubject<ICacheEvent>
    {
        private readonly KeyedSubject<ICacheEvent> _inner = new(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        public ManualResetEventSlim Joining { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            Joining.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return _inner.Subscribe(observer);
        }

        public void OnNext(ICacheEvent value) => _inner.OnNext(value);

        public void OnCompleted() => _inner.OnCompleted();

        public void Invalidate(MissedEventsReason reason) => _inner.Invalidate(reason);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class KeptEntry : IKeyedObserver<ICacheEvent>, IMissedEventsObserver
    {
        public TaskCompletionSource Missed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => "order:1";

        public bool Throws { get; init; }

        public void OnNext(ICacheEvent value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void OnEventsMissed(MissedEventsReason reason)
        {
            Missed.TrySetResult();
            if (Throws)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }
}
