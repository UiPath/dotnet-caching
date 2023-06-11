using System.Reactive.Subjects;
using System.Text;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubTopicTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private readonly List<ICacheEvent> _onNextMessages = new();

    private TopicKey _topicKey;
    private ISubscriber _subscriber = default!;
    private IObserver<ICacheEvent> _observer = default!;
    private bool _onCompleted = false;
    Action<RedisChannel, RedisValue>? _handler = null;
    private TestCacheEventFormatterProxy _formatter = default!;
    private IDatabase _database = default!;
    private string _channel = default!;
    private string _value = default!;
    private IPolicyHolder _policyHolder = default!;

    private RedisPubSubTopic<ICacheEvent>? _sut = null;
    private RedisPubSubTopic<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisPubSubTopic<ICacheEvent>>();

    [Fact]
    public void It_Subscribes_to_subscriber()
    {
        var disposable = Sut.Subscribe(_observer);
        disposable.Should().NotBeNull();
        _subscriber.Received(1).Subscribe(Arg.Is<RedisChannel>(rc => rc.ToString().EndsWith((string)_topicKey)), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Message_received_when_channel_event_received()
    {
        var disposable = Sut.Subscribe(_observer);
        var machineName = _fixture.Create<string>();

        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri($"urn:{machineName}")
        };
        var bytes = _formatter.Encode(cloudEvent);
        var message = Encoding.UTF8.GetString(bytes.Span);
        _handler?.Invoke(_topicKey.Name, message);
        _onNextMessages.FirstOrDefault().Should().BeEquivalentTo(cloudEvent);
        _onCompleted.Should().BeFalse();
    }

    [Fact]
    public void No_message_when_subscription_disposed()
    {
        var disposable = Sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        Sut.Dispose();
        _handler?.Invoke(_topicKey.Name, message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeTrue();
    }

    [Fact]
    public void No_message_when_channel_is_unknwon()
    {
        var disposable = Sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        RedisChannel newChannel = _fixture.Create<string>();

        _handler?.Invoke(newChannel, message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeFalse();
    }


    [Fact]
    public void Exception_is_thrown_when_subscribe_fails()
    {

        TopicKey channel = _fixture.Create<string>();
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(x => { throw new Exception(); });

        Action act = () => _fixture.Create<RedisPubSubTopic<ICacheEvent>>();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Subscriber_is_called_for_specific_redis_channel()
    {
        string? actualRedisChannel = null;
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => actualRedisChannel = c.Arg<RedisChannel>().ToString());

        var disposable = Sut.Subscribe(_observer);

        _subscriber.Received(1).Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        _subscriber.DidNotReceive().SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        disposable.Should().NotBeNull();
        _topicKey.Name.Should().BeEquivalentTo(actualRedisChannel);
    }

    [Fact]
    public void Unsubscribe_is_called_when_subscriber_is_disposed()
    {

        string? actualRedisChannel = null;
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => actualRedisChannel = c.Arg<RedisChannel>().ToString());

        Sut.Dispose();

        _subscriber.Received(1).Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        _topicKey.Name.Should().BeEquivalentTo(actualRedisChannel);
    }
    [Fact]
    public async Task Publish_works_as_expected()
    {
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        _database.ClearReceivedCalls();
        var executed = false;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(c =>
            {
                executed = true;
                return _fixture.Create<long>();
            });
        await Sut.PublishAsync(cloudEvent);
        executed.Should().BeTrue();
    }


    [Fact]
    public async Task Canceling_token_stops_execution()
    {
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> act = async () => { await Sut.PublishAsync(cloudEvent, token); };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task No_exceptions_are_thrown_when_redis_fails()
    {
        TopicKey topicKey = _fixture.Create<string>();
        var cloudEvent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        _database.ClearReceivedCalls();
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        Func<Task> act = async () => { await Sut.PublishAsync(cloudEvent); };
        await act.Should().NotThrowAsync();
        _channel.Should().BeNull();
        _value.Should().BeNull();
    }


    public Task DisposeAsync()
    {
        //do nothing;
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
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
        _fixture.Inject<Func<ISubject<ICacheEvent>>>(() => new Subject<ICacheEvent>());
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        var noOpExecutor = new NoOpExecutor();
        _policyHolder.Read.Returns(noOpExecutor);
        _policyHolder.Write.Returns(noOpExecutor);
        _subscriber = _fixture.Freeze<ISubscriber>();
        _observer = _fixture.Freeze<IObserver<ICacheEvent>>();
        _observer.When(x => x.OnNext(Arg.Any<ICacheEvent>()))
            .Do(c => _onNextMessages.Add(c.Arg<ICacheEvent>()));
        _observer.When(x => x.OnCompleted())
            .Do(c => _onCompleted = true);
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => _handler = c.Arg<Action<RedisChannel, RedisValue>>());
        _formatter = new TestCacheEventFormatterProxy();
        _fixture.Inject<IEventFormatterProxy<ICacheEvent>>(_formatter);
        return Task.CompletedTask;
    }
}
