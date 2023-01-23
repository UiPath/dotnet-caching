using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisChannelSubscriberTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private ISubscriber _subscriber = default!;
    private IObserver<IClearCacheEvent> _observer = default!;

    [Fact]
    public void Subscriber_is_called_for_specific_redis_channel()
    {
        var sut = _fixture.Create<RedisChannelSubscriber>();
        Channel channel = _fixture.Create<string>();
        string? actualRedisChannel = null;
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => actualRedisChannel = c.Arg<RedisChannel>().ToString());

        var disposable = sut.Subscribe(channel, _observer);

        _subscriber.Received(1).Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        _subscriber.DidNotReceive().SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        disposable.Should().NotBeNull();
        channel.Name.Should().BeEquivalentTo(actualRedisChannel);
    }

    [Fact]
    public void Unsubscribe_is_called_when_subscriber_is_disposed()
    {
        var sut = _fixture.Create<RedisChannelSubscriber>();
        Channel channel = _fixture.Create<string>();
        string? actualRedisChannel = null;
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(c => actualRedisChannel = c.Arg<RedisChannel>().ToString());

        sut.Subscribe(channel, _observer).Dispose();

        _subscriber.Received(1).Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        channel.Name.Should().BeEquivalentTo(actualRedisChannel);
    }


    [Fact]
    public void Exception_is_thrown_when_subscribe_fails()
    {
        var sut = _fixture.Create<RedisChannelSubscriber>();
        Channel channel = _fixture.Create<string>();
        _subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(x => { throw new Exception(); });


        Action act = () => sut.Subscribe(channel, _observer);
        act.Should().Throw<Exception>();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _subscriber = _fixture.Freeze<ISubscriber>();
        _observer = _fixture.Freeze<IObserver<IClearCacheEvent>>();
        _fixture.Inject<IEventFormatterProxy>(new TestEventFormatterProxy());
        return Task.CompletedTask;
    }
}
