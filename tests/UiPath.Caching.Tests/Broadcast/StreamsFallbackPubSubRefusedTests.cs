using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>A topic on the Pub/Sub fallback whose server refuses Pub/Sub too is turned off, and a Pub/Sub topic the app chose is left as it was.</summary>
public sealed class StreamsFallbackPubSubRefusedTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TopicKey Key = "orders";

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly System.Threading.Channels.Channel<bool> _changes = System.Threading.Channels.Channel.CreateUnbounded<bool>();

    private readonly ITestContextAccessor _testContextAccessor;
    private readonly List<IDisposable> _disposables = [];
    private readonly RecordedLogger _log = new();
    private readonly RecordedLogger _pubSubLog = new();
    private readonly IRedisConnector _connector;
    private readonly IDatabase _publishDatabase = Substitute.For<IDatabase>();
    private readonly ISubscriber _subscriber = Substitute.For<ISubscriber>();
    private readonly BroadcastCapability _capability;
    private readonly RedisPubSubTopicProvider _pubSub;

    public StreamsFallbackPubSubRefusedTests(ITestContextAccessor testContextAccessor)
    {
        _telemetry.EventRecorded += _ => _changes.Writer.TryWrite(true);
        _testContextAccessor = testContextAccessor;
        _connector = StreamsFakes.Connector(out var database, out _);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_publishDatabase);
        multiplexer.TimeoutMilliseconds.Returns(5000);
        database.Multiplexer.Returns(multiplexer);
        _subscriber.Multiplexer.Returns(multiplexer);
        _connector.Subscriber.Returns(_subscriber);
        _connector.IsConnected.Returns(true);
        _publishDatabase.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(1L);
        _capability = StreamsFakes.Capability(_connector);
        _pubSub = PubSub();
        _disposables.Add(_pubSub);
    }

    private CancellationToken Token => _testContextAccessor.Current.CancellationToken;

    private int Errors => _log.Records.Count(r => r.Level == LogLevel.Error);

    [Fact]
    public async Task A_refused_publish_on_the_fallback_turns_the_topic_off_with_one_Lost_and_one_Error()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(StreamsFakes.UnknownCommand());
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);

        (await sut.PublishAsync(Event(), Token)).Should().BeTrue("Off publishes true, like NullTopic");

        sut.OnStreams.Should().BeFalse();
        sut.IsSubscribed.Should().BeFalse();
        _capability.PubSubIsRefused.Should().BeTrue();
        // The queued decision may be the one that switched, and it tells the observer a moment after the publish returns.
        await WaitForAsync(() => observer.Missed.Contains(MissedEventsReason.Lost));
        observer.Missed.Count(r => r == MissedEventsReason.Lost).Should().Be(1);
        Errors.Should().Be(1);
        (await sut.PublishAsync(Event(), Token)).Should().BeTrue("Off is sticky");
        await _publishDatabase.Received(1).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_publish_refused_on_Streams_and_then_on_Pub_Sub_ends_off_and_returns_true()
    {
        PublishFails(StreamsFakes.UnknownCommand());
        var streams = Substitute.For<ITopic<ICacheEvent>>();
        ValueTaskStubs.Returns(streams.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()), _ =>
        {
            _capability.Refused(StreamsFakes.UnknownCommand());
            return new ValueTask<bool>(false);
        });
        var sut = Decorator(streams: streams);

        (await sut.PublishAsync(Event(), Token)).Should().BeTrue("the write that found out is not reported as lost");

        sut.OnStreams.Should().BeFalse();
        _capability.PubSubIsRefused.Should().BeTrue();
        Errors.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_NOPERM_publish_does_the_same()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User app has no permissions to run the 'publish' command"));
        var sut = Decorator();

        (await sut.PublishAsync(Event(), Token)).Should().BeTrue();

        _capability.PubSubIsRefused.Should().BeTrue();
        Errors.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_subscription_turns_the_topic_off_with_one_Lost_and_one_Error()
    {
        Refuse(StreamsFakes.UnknownCommand());
        // The refusal waits for the observer: a topic that turns off before anyone listens has no one to tell of the loss.
        using var listening = new ManualResetEventSlim();
        SubscribeFails(StreamsFakes.UnknownCommand(), listening);
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);
        listening.Set();

        await WaitForAsync(() => _capability.PubSubIsRefused);
        await WaitForAsync(() => observer.Missed.Contains(MissedEventsReason.Lost));
        _clock.Advance(TimeSpan.FromMilliseconds(250));

        sut.IsSubscribed.Should().BeFalse();
        (await sut.PublishAsync(Event(), Token)).Should().BeTrue("Off publishes true");
        observer.Missed.Count(r => r == MissedEventsReason.Lost).Should().Be(1);
        Errors.Should().Be(1, "the subscription is retried by the shared topic, and the refusal is still one Error");
        await _publishDatabase.DidNotReceive().PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_publish_that_times_out_leaves_the_topic_on_Pub_Sub()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(new RedisTimeoutException(CommandFlags.None, "slow", CommandStatus.Unknown));
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);

        (await sut.PublishAsync(Event(), Token)).Should().BeFalse("the publish failed and nothing says Pub/Sub is refused");

        StaysOnPubSub(sut, observer);
    }

    [Fact]
    public async Task A_publish_to_a_loading_server_leaves_the_topic_on_Pub_Sub()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "LOADING Redis is loading the dataset in memory"));
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);

        (await sut.PublishAsync(Event(), Token)).Should().BeFalse();

        StaysOnPubSub(sut, observer);
    }

    [Fact]
    public async Task A_client_side_command_error_is_not_a_refusal()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(new RedisCommandException("The specified command is not available on this connection"));
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);

        (await sut.PublishAsync(Event(), Token)).Should().BeFalse();

        StaysOnPubSub(sut, observer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_subscription_that_times_out_or_meets_a_loading_server_leaves_the_topic_on_Pub_Sub(bool timeout)
    {
        Refuse(StreamsFakes.UnknownCommand());
        SubscribeFails(timeout ? new RedisTimeoutException(CommandFlags.None, "slow", CommandStatus.Unknown) : new RedisServerException(RedisErrorKind.None, CommandFlags.None, "LOADING Redis is loading the dataset in memory"));
        var sut = Decorator();
        var observer = new Observer();
        sut.Subscribe(observer);

        _clock.Advance(TimeSpan.Zero);
        _clock.Advance(TimeSpan.FromMilliseconds(400));

        StaysOnPubSub(sut, observer);
        _subscriber.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISubscriber.Subscribe)).Should().BeGreaterThan(1, "the topic keeps trying");
    }

    [Fact]
    public async Task A_second_topic_on_the_connector_starts_off()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(StreamsFakes.UnknownCommand());
        var first = Decorator();
        await first.PublishAsync(Event(), Token);

        var second = Decorator("invoices");

        second.OnStreams.Should().BeFalse();
        second.IsSubscribed.Should().BeFalse();
        (await second.PublishAsync(Event(), Token)).Should().BeTrue();
        ((IPubSubSubscriptions)_pubSub).Channels().Should().ContainSingle("the second topic never asked the provider for a Pub/Sub topic").Which.Should().EndWith("orders");
        Errors.Should().Be(1, "one Error per connector");
    }

    [Fact]
    public async Task A_Pub_Sub_topic_the_app_chose_keeps_its_logging_and_retry_when_its_publish_is_refused()
    {
        var factory = new TopicFactory(Options.Create(new CacheOptions { AppShortName = "app" }), [_pubSub]);
        var native = factory.Get(KnownTopicNames.RedisPubSub).Create(Key);
        PublishFails(StreamsFakes.UnknownCommand());

        (await native.PublishAsync(Event(), Token)).Should().BeFalse("a Pub/Sub topic that cannot publish says so, as it always did");
        (await native.PublishAsync(Event(), Token)).Should().BeFalse();

        _pubSubLog.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("Error when publishing", StringComparison.Ordinal)).Should().Be(2);
        _capability.PubSubIsRefused.Should().BeFalse("nothing hooked in, so no connector state changed");
        Errors.Should().Be(0);
        await _publishDatabase.Received(2).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_topic_shared_with_a_native_user_is_not_disposed_or_changed_when_the_fallback_turns_off()
    {
        var native = _pubSub.Create(Key);
        Refuse(StreamsFakes.UnknownCommand());
        var sut = Decorator();
        sut.Subscribe(new Observer());
        PublishFails(StreamsFakes.UnknownCommand());

        (await sut.PublishAsync(Event(), Token)).Should().BeTrue();

        _pubSub.Create(Key).Should().BeSameAs(native, "the fallback asked for the shared topic and made no topic of its own");
        ((IPubSubSubscriptions)_pubSub).Channels().Should().ContainSingle();
        _subscriber.DidNotReceive().Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>?>(), Arg.Any<CommandFlags>());
        (await native.PublishAsync(Event(), Token)).Should().BeFalse("its own publish is refused and logged as before");
        _pubSubLog.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("Error when publishing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_topic_that_left_Pub_Sub_stops_listening_to_the_shared_topic()
    {
        Refuse(StreamsFakes.UnknownCommand());
        PublishFails(StreamsFakes.UnknownCommand());
        var sut = Decorator();
        await sut.PublishAsync(Event(), Token);
        sut.Dispose();

        // Its handler is gone, so a refusal now reaches nobody: the latch holds the one Error.
        (await _pubSub.Create(Key).PublishAsync(Event(), Token)).Should().BeFalse();

        Errors.Should().Be(1);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables.AsEnumerable().Reverse())
        {
            disposable.Dispose();
        }
    }

    private static TestCacheEvent Event() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Source = new Uri("urn:test"),
    };

    private void StaysOnPubSub(StreamsFallbackTopic sut, Observer observer)
    {
        sut.OnStreams.Should().BeFalse();
        _capability.PubSubIsRefused.Should().BeFalse();
        observer.Missed.Should().NotContain(MissedEventsReason.Lost);
        Errors.Should().Be(0);
    }

    private void Refuse(Exception error) => _capability.Refused(error);

    private void PublishFails(Exception error) =>
        _publishDatabase.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).ThrowsAsync(error);

    private void SubscribeFails(Exception error, ManualResetEventSlim? after = null) =>
        _subscriber.When(s => s.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())).Do(_ =>
        {
            after?.Wait(Token);
            throw error;
        });

    private StreamsFallbackTopic Decorator(string key = "orders", ITopic<ICacheEvent>? streams = null)
    {
        var topic = new StreamsFallbackTopic(
            key,
            _capability,
            StreamsFakes.Settings(StreamsUnsupportedAction.PubSub, logger: _log),
            _pubSub,
            _ => streams ?? Substitute.For<ITopic<ICacheEvent>>(),
            new KeyedSubject<ICacheEvent>(NullLogger<KeyedSubject<ICacheEvent>>.Instance),
            NullLogger.Instance,
            telemetry: _telemetry);
        _disposables.Add(topic);
        return topic;
    }

    private RedisPubSubTopicProvider PubSub()
    {
        var loggers = Substitute.For<ILoggerFactory>();
        loggers.CreateLogger(Arg.Any<string>()).Returns(_pubSubLog);
        return new RedisPubSubTopicProvider(
            Options.Create(new RedisPubSubTopicOptions { Enabled = true, SubscriberTimeout = TimeSpan.FromMilliseconds(50), SubscriberDueTime = TimeSpan.Zero, ConnectionMonitorEnabled = false }),
            Options.Create(new CacheOptions { AppShortName = "app" }),
            new PerTopicOptionsRegistry<RedisPubSubTopicOptions>(new ConfigurationBuilder().Build().GetSection("Topics")),
            _connector,
            new TestCacheEventFormatterProxy(),
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            Substitute.For<IRedisProfiler>(),
            loggers,
            _clock);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        _clock.Advance(TimeSpan.Zero);
        while (!condition())
        {
            await _changes.Reader.ReadAsync(Token);
        }
        condition().Should().BeTrue();
    }

    private sealed class Observer : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        private readonly object _gate = new();
        private readonly List<MissedEventsReason> _missed = [];

        public IReadOnlyList<MissedEventsReason> Missed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _missed];
                }
            }
        }

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
            lock (_gate)
            {
                _missed.Add(reason);
            }
        }
    }
}
