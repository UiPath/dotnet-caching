using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubSubjectWriterTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ISubscriber _subscriber = default!;
    private ISubject<ICacheEvent> _subject = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private RedisChannel _channel = default!;

    private RedisPubSubSubjectWriter<ICacheEvent>? _sut = null;
    private RedisPubSubSubjectWriter<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisPubSubSubjectWriter<ICacheEvent>>();

    [Fact]
    public void Reiceive_redis_null()
    {
        Action<RedisChannel, RedisValue>? action = null!;
        _subscriber.When(x => x.Subscribe(_channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = Sut;
        action.Should().NotBeNull();
        action!(_channel, RedisValue.Null);
        _subject.Received(0).OnNext(Arg.Any<ICacheEvent>());
    }

    [Fact]
    public void Reiceive_no_json()
    {
        Action<RedisChannel, RedisValue>? action = null!;
        _subscriber.When(x => x.Subscribe(_channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = Sut;
        action.Should().NotBeNull();
        action!(_channel, (RedisValue)_fixture.Create<string>());
        _subject.Received(0).OnNext(Arg.Any<ICacheEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reiceive_event(bool valid)
    {
        Action<RedisChannel, RedisValue>? action = null!;
        var expected = _fixture.Create<TestCacheEvent>();
        expected.Valid = valid;
        var str = JsonSerializer.Serialize(expected);
        var expectedString = Encoding.UTF8.GetBytes(str);
        _subscriber.When(x => x.Subscribe(_channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = Sut;
        action.Should().NotBeNull();
        var expectedCalls = valid ? 1 : 0;

        action!(_channel, (RedisValue)expectedString);
        _subject.Received(expectedCalls).OnNext(Arg.Any<ICacheEvent>());
    }

    [Fact]
    public void Dispose_works()
    {
        var act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_unsubscribe_exception()
    {
        _subscriber.When(x => x.Unsubscribe(_channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
        .Throw<Exception>();
        var act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _channel = RedisChannel.Literal(_fixture.Create<string>());
        _fixture.Inject(_channel);
        _formatter = new CacheClearEventFormatterProxy();
        _subject = _fixture.Freeze<ISubject<ICacheEvent>>();
        _subscriber = _fixture.Freeze<ISubscriber>();
        _fixture.Inject(_formatter);
        return Task.CompletedTask;
    }
}
