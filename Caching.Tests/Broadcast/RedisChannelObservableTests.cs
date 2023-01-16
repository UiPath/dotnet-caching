using System.Text;
using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisChannelObservableTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private readonly List<CloudEvent> _onNextMessages = new();
    private readonly CloudEventFormatter _formatter = new JsonEventFormatter<ClearCacheEventData>();

    private Channel _channel;
    private ISubscriber _subscriber = default!;
    private IObserver<CloudEvent> _observer = default!;
    private bool _onCompleted = false;
    Action<RedisChannel, RedisValue>? _handler = null;

    [Fact]
    public void It_Subscribes_to_subscriber()
    {
        var sut = _fixture.Create<RedisChannelObservable>();
        var disposable = sut.Subscribe(_observer);
        disposable.Should().NotBeNull();
        _subscriber.Received(1).Subscribe(Arg.Is<RedisChannel>(rc => rc.ToString() == (string)_channel), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Message_received_when_channel_event_received()
    {
        var sut = _fixture.Create<RedisChannelObservable>();
        var disposable = sut.Subscribe(_observer);

        var cloudEvent = new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Type = "ClearCache",
            Source = new Uri("urn:machine"),
            DataContentType = "application/json",
            Data = new ClearCacheEventData(Guid.NewGuid().ToString())
        };
        var bytes = _formatter.EncodeStructuredModeMessage(cloudEvent, out _);
        var message = Encoding.UTF8.GetString(bytes.Span);
        _handler?.Invoke(_channel.Name, message);
        _onNextMessages.FirstOrDefault().Should().BeEquivalentTo(cloudEvent);
        _onCompleted.Should().BeFalse();
    }

    [Fact]
    public void No_message_when_subscription_disposed()
    {
        var sut = _fixture.Create<RedisChannelObservable>();
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
        var sut = _fixture.Create<RedisChannelObservable>();
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
        _observer = _fixture.Freeze<IObserver<CloudEvent>>();
        _observer.When(x => x.OnNext(Arg.Any<CloudEvent>()))
            .Do(c => _onNextMessages.Add(c.Arg<CloudEvent>()));
        _observer.When(x => x.OnCompleted())
            .Do(c => _onCompleted = true);
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => _handler = c.Arg<Action<RedisChannel, RedisValue>>());

        _fixture.Inject(_formatter);
        return Task.CompletedTask;
    }
}
