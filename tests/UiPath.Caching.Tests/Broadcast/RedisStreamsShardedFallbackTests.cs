#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Logging;
using NSubstitute.Extensions;
using StackExchange.Redis;
using UiPath.Caching.Policies;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>A server that refuses sharded Pub/Sub moves the Streams doorbell to the literal channel of the same name, once per connector.</summary>
public class RedisStreamsShardedFallbackTests : IAsyncLifetime
{
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly System.Threading.Channels.Channel<bool> _changes = System.Threading.Channels.Channel.CreateUnbounded<bool>();
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly List<RedisStreamsTopic<ICacheEvent>> _topics = [];
    private readonly List<RedisChannel> _subscribed = [];
    private readonly List<RedisChannel> _published = [];
    private readonly RecordedLogger _recorded = new();
    private readonly CancellationTokenSource _stop = new();
    private RedisStreamsTopicOptions _options = default!;
    private IRedisConnector _redis = default!;
    private IDatabase _database = default!;
    private ISubscriber _subscriber = default!;
    private Exception? _shardedError;

    public ValueTask InitializeAsync()
    {
        _fixture.Inject<TimeProvider>(_clock);
        _fixture.Customize<RedisStreamsTopic<ICacheEvent>>(c => c.FromFactory(new AutoFixture.Kernel.MethodInvoker(new AutoFixture.Kernel.GreedyConstructorQuery())));
        _database = _fixture.Freeze<IDatabase>();
        _database.Configure().StreamInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(default(StreamInfo)));
        _database.Configure().StreamGroupInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(call =>
        {
            var channel = call.Arg<RedisChannel>();
            lock (_published)
            {
                _published.Add(channel);
                _changes.Writer.TryWrite(true);
            }

            return channel.IsSharded && _shardedError is not null ? Task.FromException<long>(_shardedError) : Task.FromResult(1L);
        });
        _subscriber = Substitute.For<ISubscriber>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.TimeoutMilliseconds.Returns(60_000);
        _subscriber.Multiplexer.Returns(multiplexer);
        _subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(call =>
        {
            var channel = call.Arg<RedisChannel>();
            lock (_subscribed)
            {
                _subscribed.Add(channel);
                _changes.Writer.TryWrite(true);
            }

            return channel.IsSharded && _shardedError is not null ? Task.FromException(_shardedError) : Task.CompletedTask;
        });
        _redis = _fixture.Freeze<IRedisConnector>();
        _redis.Database.Returns(_database);
        _redis.Subscriber.Returns(_subscriber);
        _redis.GetPrimaries().Returns([]);
        _fixture.Freeze<IConnectionState>().IsConnected.Returns(true);
        _fixture.Inject<Func<IEventSubject<ICacheEvent>>>(() => Substitute.For<IEventSubject<ICacheEvent>>());
        var noOp = new EmptyResiliencePipeline();
        var pipelines = _fixture.Freeze<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(noOp);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(noOp);
        _fixture.Inject(_stop.Token);
        _fixture.Inject<ILogger<RedisStreamsTopic<ICacheEvent>>>(new RecordedLogger<RedisStreamsTopic<ICacheEvent>>(_recorded));
        _options = _fixture.Create<RedisStreamsTopicOptions>();
        _options.NotifiedPollInterval = null;
        _options.NotifyEnabled = true;
        _options.NotifyShardedPubSub = true;
        _options.NotifyChannelStrategy = null;
        _options.NotifyMinInterval = null;
        // Due at once, then idle: the subscribe is the test's to watch, not a retry's.
        _options.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _options.NotifySubscriberDueTime = TimeSpan.FromMilliseconds(10);
        _options.PollInterval = TimeSpan.FromSeconds(60);
        _fixture.Inject(_options);
        _fixture.Inject(_fixture.Create<CacheOptions>());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _stop.Cancel();
        foreach (var topic in _topics)
        {
            topic.Dispose();
        }

        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_refused_ssubscribe_moves_the_topic_to_the_literal_channel_and_subscribes_it()
    {
        _shardedError = StreamsFakes.UnknownCommand();

        _ = NewTopic();
        await WaitAsync(() => Subscribed().Any(c => !c.IsSharded));

        var channels = Subscribed();
        channels[0].IsSharded.Should().BeTrue();
        channels.Count(c => !c.IsSharded).Should().Be(1);
        channels.Count(c => c.IsSharded).Should().Be(1, "the sharded channel is tried once");
        BroadcastCapability.For(_redis).ShardedPubSubIsRefused.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_spublish_moves_the_next_doorbell_to_the_literal_channel()
    {
        _shardedError = StreamsFakes.NoPermission();
        _options.NotifySubscriberDueTime = TimeSpan.FromMinutes(5);
        var topic = NewTopic();
        var token = TestContext.Current.CancellationToken;

        (await topic.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await WaitAsync(() => Published().Count >= 1);
        // The refused doorbell has settled once the switch is made; the next entry rings the literal channel.
        await WaitAsync(() => BroadcastCapability.For(_redis).ShardedPubSubIsRefused);
        (await topic.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await WaitAsync(() => Published().Any(c => !c.IsSharded));

        var channels = Published();
        channels[0].IsSharded.Should().BeTrue();
        channels.Count(c => c.IsSharded).Should().Be(1, "after the switch nothing goes out on the sharded channel");
        (await topic.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await WaitAsync(() => Published().Count(c => !c.IsSharded) >= 2);
        Published().Count(c => c.IsSharded).Should().Be(1);
    }

    [Fact]
    public async Task The_switch_is_made_and_logged_once_per_connector_and_a_later_topic_starts_literal()
    {
        _shardedError = StreamsFakes.UnknownCommand();
        _ = NewTopic();
        await WaitAsync(() => Subscribed().Any(c => !c.IsSharded));
        var before = Subscribed().Count;

        var second = NewTopic(new TopicKey("second"));
        await WaitAsync(() => Subscribed().Count > before);
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        Subscribed().Skip(before).Should().OnlyContain(c => !c.IsSharded, "the connector already knows sharded Pub/Sub is refused");
        (await second.PublishAsync(_fixture.Create<ICacheEvent>(), TestContext.Current.CancellationToken)).Should().BeTrue();
        await WaitAsync(() => Published().Count >= 1);
        Published().Should().OnlyContain(c => !c.IsSharded);
        _recorded.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("Sharded Pub/Sub was refused", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Two_topics_refused_together_log_one_warning()
    {
        _shardedError = StreamsFakes.UnknownCommand();
        _ = NewTopic();
        _ = NewTopic(new TopicKey("second"));

        await WaitAsync(() => Subscribed().Count(c => !c.IsSharded) >= 2);
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        _recorded.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("Sharded Pub/Sub was refused", StringComparison.Ordinal)).Should().Be(1);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("client")]
    [InlineData("loading")]
    public async Task An_error_that_is_not_a_refusal_does_not_switch(string kind)
    {
        _shardedError = kind switch
        {
            "timeout" => new RedisTimeoutException(CommandFlags.None, "Timeout awaiting response", CommandStatus.Unknown),
            "client" => new RedisCommandException("This command is not available on this server"),
            _ => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "LOADING Redis is loading the dataset in memory"),
        };
        var topic = NewTopic();
        await WaitAsync(() => Subscribed().Count >= 1);
        (await topic.PublishAsync(_fixture.Create<ICacheEvent>(), TestContext.Current.CancellationToken)).Should().BeTrue();
        await WaitAsync(() => Published().Count >= 1);
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        Subscribed().Should().OnlyContain(c => c.IsSharded);
        Published().Should().OnlyContain(c => c.IsSharded);
        BroadcastCapability.For(_redis).ShardedPubSubIsRefused.Should().BeFalse();
    }

    [Fact]
    public async Task A_custom_channel_strategy_is_not_switched()
    {
        // Its channel is the operator's own; a literal one of ours may not be where they listen.
        _shardedError = StreamsFakes.UnknownCommand();
        var strategy = Substitute.For<IRedisChannelStrategy>();
        strategy.GetRedisChannel(Arg.Any<TopicKey>()).Returns(RedisChannel.Sharded("custom{1}"));
        _options.NotifyChannelStrategy = strategy;
        var topic = NewTopic();
        await WaitAsync(() => Subscribed().Count >= 1);
        (await topic.PublishAsync(_fixture.Create<ICacheEvent>(), TestContext.Current.CancellationToken)).Should().BeTrue();
        await WaitAsync(() => Published().Count >= 1);
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        Subscribed().Should().OnlyContain(c => c.IsSharded);
        Published().Should().OnlyContain(c => c.IsSharded);
        BroadcastCapability.For(_redis).ShardedPubSubIsRefused.Should().BeFalse();
    }

    private async Task WaitAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await _changes.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        condition().Should().BeTrue();
    }

    private RedisStreamsTopic<ICacheEvent> NewTopic(TopicKey? key = null)
    {
        _fixture.Inject(key ?? new TopicKey("first"));
        var topic = _fixture.Create<RedisStreamsTopic<ICacheEvent>>();
        _topics.Add(topic);
        if (_options.NotifySubscriberDueTime < TimeSpan.FromMinutes(1))
        {
            _clock.Advance(_options.NotifySubscriberDueTime!.Value);
            _clock.Advance(_options.NotifySubscriberDueTime!.Value);
        }
        return topic;
    }

    private List<RedisChannel> Subscribed()
    {
        lock (_subscribed)
        {
            return [.. _subscribed];
        }
    }

    private List<RedisChannel> Published()
    {
        lock (_published)
        {
            return [.. _published];
        }
    }

    private sealed class RecordedLogger<T>(RecordedLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
