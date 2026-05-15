using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions.Extensions;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubSubjectWriterTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ISubscriber _subscriber = default!;
    private Channel<ICacheEvent> _channel = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private RedisChannel _redisChannel = default!;
    private RedisPubSubTopicOptions _options = default!;
    private readonly TimeSpan _delay = 50.Milliseconds();
    private Action<RedisChannel, RedisValue>? _capturedAction;
    private readonly TaskCompletionSource _subscribeCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RedisPubSubSubjectWriter<ICacheEvent>? _sut = null;

    private RedisPubSubSubjectWriter<ICacheEvent> Sut() =>
        _sut ??= _fixture.Create<RedisPubSubSubjectWriter<ICacheEvent>>();

    private Task<Action<RedisChannel, RedisValue>> WaitForSubscribeAsync() =>
        _subscribeCalled.Task
            .WaitAsync(TimeSpan.FromSeconds(2), testContextAccessor.Current.CancellationToken)
            .ContinueWith(_ => _capturedAction!, TaskContinuationOptions.OnlyOnRanToCompletion);

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
            SubscriberDueTime = TimeSpan.Zero
        };
        _fixture.Inject(_options);
        return ValueTask.CompletedTask;
    }
}
