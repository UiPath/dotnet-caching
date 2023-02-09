using System.Text;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisChannelObservableTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private readonly List<IPubSubEvent> _onNextMessages = new();

    private Channel _channel;
    private ISubscriber _subscriber = default!;
    private IObserver<IPubSubEvent> _observer = default!;
    private bool _onCompleted = false;
    Action<RedisChannel, RedisValue>? _handler = null;
    private PubSubEventFormatterProxy _formatter = default!;

    [Fact]
    public void It_Subscribes_to_subscriber()
    {
        var sut = _fixture.Create<RedisChannelObservable<IPubSubEvent>>();
        var disposable = sut.Subscribe(_observer);
        disposable.Should().NotBeNull();
        _subscriber.Received(1).Subscribe(Arg.Is<RedisChannel>(rc => rc.ToString() == (string)_channel), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Message_received_when_channel_event_received()
    {
        var sut = _fixture.Create<RedisChannelObservable<IPubSubEvent>>();
        var disposable = sut.Subscribe(_observer);

        var cloudEvent = new TestPubSubEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        var bytes = _formatter.Encode(cloudEvent);
        var message = Encoding.UTF8.GetString(bytes.Span);
        _handler?.Invoke(_channel.Name, message);
        _onNextMessages.FirstOrDefault().Should().BeEquivalentTo(cloudEvent);
        _onCompleted.Should().BeFalse();
    }

    [Fact]
    public void No_message_when_subscription_disposed()
    {
        var sut = _fixture.Create<RedisChannelObservable<IPubSubEvent>>();
        var disposable = sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        disposable.Dispose();
        _handler?.Invoke(_channel.Name, message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeTrue();
    }

    [Fact]
    public void No_message_when_channel_is_unknwon()
    {
        var sut = _fixture.Create<RedisChannelObservable<IPubSubEvent>>();
        var disposable = sut.Subscribe(_observer);
        var message = _fixture.Create<string>();
        RedisChannel newChannel = _fixture.Create<string>();

        _handler?.Invoke(newChannel, message);
        _onNextMessages.Should().BeEmpty();
        _onCompleted.Should().BeFalse();
    }


    public Task DisposeAsync()
    {
        //do nothing;
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _channel = _fixture.Freeze<Channel>();
        _subscriber = _fixture.Freeze<ISubscriber>();
        _observer = _fixture.Freeze<IObserver<IPubSubEvent>>();
        _observer.When(x => x.OnNext(Arg.Any<IPubSubEvent>()))
            .Do(c => _onNextMessages.Add(c.Arg<IPubSubEvent>()));
        _observer.When(x => x.OnCompleted())
            .Do(c => _onCompleted = true);
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => _handler = c.Arg<Action<RedisChannel, RedisValue>>());
        _formatter = new PubSubEventFormatterProxy();
        _fixture.Inject<IEventFormatterProxy<IPubSubEvent>>(_formatter);
        return Task.CompletedTask;
    }
}
