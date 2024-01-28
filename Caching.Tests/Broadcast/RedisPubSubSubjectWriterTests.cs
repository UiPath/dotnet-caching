using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions.Extensions;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubSubjectWriterTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ISubscriber _subscriber = default!;
    private Channel<ICacheEvent> _channel = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private RedisChannel _redisChannel = default!;
    private RedisPubSubTopicOptions _options = default!;
    private readonly TimeSpan _delay = 50.Milliseconds();

    private RedisPubSubSubjectWriter<ICacheEvent>? _sut = null;

    private async Task<RedisPubSubSubjectWriter<ICacheEvent>> Sut()
    {
        if(_sut != null)
        {
            return _sut;
        }
        _sut = _fixture.Create<RedisPubSubSubjectWriter<ICacheEvent>>();
        await Task.Delay(_delay.Multiply(2));
        return _sut;
    }

    [Fact]
    public async Task Reiceive_redis_null()
    {
        Action<RedisChannel, RedisValue>? action = null!;
        
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = await Sut();
        action.Should().NotBeNull();
        action!(_redisChannel, RedisValue.Null);
        await Task.Delay(100);
        _channel.Reader.TryRead(out var item).Should().BeFalse();
    }

    [Fact]
    public async Task Reiceive_no_json()
    {
        Action<RedisChannel, RedisValue>? action = null!;
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = await Sut();
        action.Should().NotBeNull();
        action!(_redisChannel, (RedisValue)_fixture.Create<string>());
        await Task.Delay(100);
        _channel.Reader.TryRead(out var item).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reiceive_event(bool valid)
    {
        Action<RedisChannel, RedisValue>? action = null!;
        var expected = _fixture.Create<TestCacheEvent>();
        expected.Valid = valid;
        var str = JsonSerializer.Serialize(expected);
        var expectedString = Encoding.UTF8.GetBytes(str);
        _subscriber.When(x => x.Subscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(ctx =>
            {
                action = ctx.Arg<Action<RedisChannel, RedisValue>>();
            });
        var sut = await Sut();
        await Task.Delay(_delay.Multiply(3));
        action.Should().NotBeNull();
        action!(_redisChannel, (RedisValue)expectedString);
        await Task.Delay(_delay.Multiply(3));
        _channel.Reader.TryRead(out var item).Should().Be(valid);
    }

    [Fact]
    public async Task Dispose_works()
    {
        var sut = await Sut();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_unsubscribe_exception()
    {
        _subscriber.When(x => x.Unsubscribe(_redisChannel, Arg.Any<Action<RedisChannel, RedisValue>>()))
        .Throw<Exception>();
        var sut = await Sut();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _redisChannel = RedisChannel.Literal(_fixture.Create<string>());
        _fixture.Inject(_redisChannel);
        _formatter = new CacheClearEventFormatterProxy();
        _channel = Channel.CreateUnbounded<ICacheEvent>();
        _subscriber = _fixture.Freeze<ISubscriber>();
        _fixture.Inject(_formatter);
        _fixture.Inject((ChannelWriter<ICacheEvent>)_channel);
        _options = new RedisPubSubTopicOptions
        {
            SubscriberTimeout = _delay,
            SubscriberDueTime = TimeSpan.Zero
        };
        _fixture.Inject(_options);
        return Task.CompletedTask;
    }
}
