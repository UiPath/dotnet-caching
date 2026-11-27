using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

public class StreamsFallbackTopicTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly System.Threading.Channels.Channel<bool> _switches = System.Threading.Channels.Channel.CreateUnbounded<bool>();
    private readonly List<IDisposable> _disposables = [];
    private readonly ITestContextAccessor _testContextAccessor;
    private readonly IDatabase _database;
    private readonly BroadcastCapability _capability;
    private readonly FakeInner _streams = new();
    private readonly FakeInner _pubSub = new();
    private readonly FakeFallback _fallback = new();
    private int _leftStreams;

    public StreamsFallbackTopicTests(ITestContextAccessor testContextAccessor)
    {
        _testContextAccessor = testContextAccessor;
        _capability = StreamsFakes.Capability(StreamsFakes.Connector(out _database, out _));
        _fallback.Topic = _pubSub.Topic;
    }

    private CancellationToken Token => _testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task The_Streams_topic_exists_and_takes_publishes_before_any_decision()
    {
        Hold();

        var sut = Create();

        _capability.State.Should().Be(StreamsState.Unknown);
        _streams.Created.Should().Be(1, "the Streams topic is made in the constructor, as the plain one was");
        _streams.Subscribers.Should().HaveCount(1);
        sut.OnStreams.Should().BeTrue();
        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();
        _streams.Published.Should().HaveCount(1);
        _pubSub.Published.Should().BeEmpty();
        _fallback.Created.Should().Be(0);
    }

    [Fact]
    public void A_Pub_Sub_refusal_met_while_the_constructor_switches_is_logged_once()
    {
        // Streams is known to be missing, so the constructor goes to Pub/Sub at once, and that topic reports its refusal as soon as the topic listens.
        _capability.Refused(StreamsFakes.UnknownCommand());
        _fallback.Topic = new RefusingTopic();
        var log = new RecordedLogger();

        using var sut = new StreamsFallbackTopic(
            "orders",
            _capability,
            StreamsFakes.Settings(StreamsUnsupportedAction.PubSub, logger: log),
            _fallback,
            onRead => _streams.Make(onRead),
            new KeyedSubject<ICacheEvent>(NullLogger<KeyedSubject<ICacheEvent>>.Instance),
            NullLogger.Instance);

        _capability.PubSubIsRefused.Should().BeTrue();
        log.Records.Count(r => r.Level == LogLevel.Error).Should().Be(1, "the refusal is logged by the settings of the topic that met it");
    }

    [Fact]
    public async Task A_probe_that_finds_Streams_supported_changes_nothing_and_sends_nothing()
    {
        var release = Hold();
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);

        release.SetResult(true);
        (await _capability.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);

        sut.OnStreams.Should().BeTrue();
        _streams.Created.Should().Be(1);
        _streams.Disposed.Should().BeFalse();
        _streams.Published.Should().BeEmpty();
        _pubSub.Published.Should().BeEmpty();
        _fallback.Created.Should().Be(0);
        observer.Missed.Should().BeEmpty("a gap would expire what an early reader just wrote");
        _streams.Subscribers.Should().HaveCount(1, "one bridge, whatever the observers");
    }

    [Fact]
    public async Task A_refusal_while_publishing_moves_the_publish_to_Pub_Sub_once()
    {
        var sut = Create();
        _streams.OnPublish = () => _capability.Refused(StreamsFakes.UnknownCommand());
        _streams.PublishResult = false;

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();

        _streams.Published.Should().HaveCount(1);
        _pubSub.Published.Should().HaveCount(1);
        sut.OnStreams.Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_tracks_one_TransportChanged_event_and_the_topic_names_its_transport()
    {
        var telemetry = new RecordingTelemetryProvider();
        var sut = Create(telemetry: telemetry);
        ((ITopic<ICacheEvent>)sut).Transport.Should().Be(Metrics.TransportStreams);
        telemetry.Events.Should().BeEmpty("starting on Streams is not a switch");
        _streams.OnPublish = () => _capability.Refused(StreamsFakes.UnknownCommand());
        _streams.PublishResult = false;

        await sut.PublishAsync(Event(null), Token);
        await sut.PublishAsync(Event(null), Token);

        // The switch may run on the thread the capability's change woke, which tracks the event after the publish returned.
        await WaitForAsync(() => telemetry.Events.Any(e => e.Name == Metrics.TransportChanged));
        var changed = telemetry.Events.Where(e => e.Name == Metrics.TransportChanged).ToList();
        changed.Should().ContainSingle();
        changed[0].Properties![Metrics.TransportFrom].Should().Be(Metrics.TransportStreams);
        changed[0].Properties![Metrics.TransportTo].Should().Be(Metrics.TransportPubSub);
        changed[0].Properties![Metrics.TransportReason].Should().NotBeNullOrEmpty();
        changed[0].Properties![Metrics.TopicName].Should().Be("orders");
        ((ITopic<ICacheEvent>)sut).Transport.Should().Be(Metrics.TransportPubSub);
    }

    [Fact]
    public async Task A_topic_turned_off_names_off_as_its_transport()
    {
        var telemetry = new RecordingTelemetryProvider();
        var sut = Create(StreamsUnsupportedAction.Disable, telemetry: telemetry);
        _streams.OnPublish = () => _capability.Refused(StreamsFakes.UnknownCommand());
        _streams.PublishResult = false;

        await sut.PublishAsync(Event(null), Token);

        // The switch may run on the thread the capability's change woke, which tracks the event after the publish returned.
        await WaitForAsync(() => telemetry.Events.Any(e => e.Name == Metrics.TransportChanged));
        ((ITopic<ICacheEvent>)sut).Transport.Should().Be(Metrics.TransportOff);
        telemetry.Events.Should().ContainSingle(e => e.Name == Metrics.TransportChanged && e.Properties![Metrics.TransportTo] == Metrics.TransportOff);
    }

    [Fact]
    public async Task A_publish_that_fails_without_a_switch_is_not_retried()
    {
        var sut = Create();
        _streams.PublishResult = false;

        (await sut.PublishAsync(Event(null), Token)).Should().BeFalse();

        _streams.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_switch_sends_each_observer_one_Lost_and_retires_the_old_topic()
    {
        var sut = Create();
        var keyed = new KeyedObserver("k");
        var broadcast = new Observer();
        sut.Subscribe(keyed);
        sut.Subscribe(broadcast);
        keyed.Missed.Clear();
        broadcast.Missed.Clear();
        Read();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => keyed.Missed.Count > 0 && broadcast.Missed.Count > 0);

        keyed.Missed.Should().Equal(MissedEventsReason.Lost);
        broadcast.Missed.Should().Equal(MissedEventsReason.Lost);
        _streams.Disposed.Should().BeTrue();
        _pubSub.Disposed.Should().BeFalse("the Pub/Sub topic may be shared");
        _leftStreams.Should().BeGreaterThanOrEqualTo(1, "its stream key is dropped");
        _pubSub.Subscribers.Should().HaveCount(1);
        _fallback.Created.Should().Be(1);
    }

    [Fact]
    public async Task A_switch_before_the_Streams_topic_ever_worked_sends_no_signal()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);

        _capability.Refused(StreamsFakes.UnknownCommand());
        await _switches.Reader.ReadAsync(Token);
        _pubSub.Subscribers.Should().HaveCount(1);

        observer.Missed.Should().BeEmpty("it is as a first attach: nothing came over Streams that could be lost");
        _streams.Disposed.Should().BeTrue();
        sut.OnStreams.Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_after_a_publish_over_Streams_succeeded_sends_Lost()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);
        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => observer.Missed.Count > 0);

        observer.Missed.Should().Equal(MissedEventsReason.Lost);
    }

    [Fact]
    public async Task A_switch_after_a_read_over_Streams_succeeded_sends_Lost()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);
        Read();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => observer.Missed.Count > 0);

        observer.Missed.Should().Equal(MissedEventsReason.Lost);
    }

    [Fact]
    public async Task A_publish_that_fails_does_not_count_as_the_Streams_topic_having_worked()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);
        _streams.PublishResult = false;
        (await sut.PublishAsync(Event(null), Token)).Should().BeFalse();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => !sut.OnStreams);

        observer.Missed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_that_finds_Streams_unsupported_switches_once()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).Returns(_ => answer.Task);
        var capability = StreamsFakes.Capability(connector);
        var telemetry = new RecordingTelemetryProvider();
        var sut = Create(capability: capability, telemetry: telemetry);
        var observer = new Observer();
        sut.Subscribe(observer);
        Read();
        answer.SetException(StreamsFakes.UnknownCommand());

        // The switch is tracked after it tells the observers what they lost, while the new topic is subscribed before that.
        await WaitForAsync(() => telemetry.Events.Any(e => e.Name == Metrics.TransportChanged));

        observer.Missed.Should().Equal(MissedEventsReason.Lost);
        _fallback.Created.Should().Be(1);
        _streams.Disposed.Should().BeTrue();
        sut.OnStreams.Should().BeFalse();
    }

    [Fact]
    public async Task Events_of_the_new_topic_reach_the_observers_and_those_of_the_retired_one_do_not()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);
        var retired = _streams.Subscribers.Single();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => _pubSub.Subscribers.Count == 1);
        // The old bridge is retired after the new one is in place, and the old topic disposed after that.
        await WaitForAsync(() => _streams.Disposed);
        retired.OnNext(Event(null));
        retired.OnCompleted();
        _pubSub.Subscribers.Single().OnNext(Event(null));

        observer.Events.Should().HaveCount(1);
        observer.Completed.Should().BeFalse("a retired topic's completion is not the observers'");
    }

    [Fact]
    public void First_and_keyed_observers_are_told_before_the_broadcast_ones()
    {
        var sut = Create();
        List<string> order = [];
        sut.Subscribe(new Observer(order, "broadcast"));
        sut.Subscribe(new KeyedObserver("k", order, "keyed"));
        sut.Subscribe(new FirstObserver(order, "first"));

        _streams.Subscribers.Single().OnNext(Event("k"));

        order.Should().Equal("first", "keyed", "broadcast");
    }

    [Fact]
    public void A_loss_the_topic_beneath_reports_reaches_the_observers()
    {
        var sut = Create();
        var observer = new Observer();
        sut.Subscribe(observer);
        observer.Missed.Clear();

        ((IMissedEventsObserver)_streams.Subscribers.Single()).OnEventsMissed(MissedEventsReason.Lost);

        observer.Missed.Should().Equal(MissedEventsReason.Lost);
    }

    [Fact]
    public async Task Without_a_fallback_an_unsupported_server_turns_broadcast_off_and_publishes_succeed()
    {
        var sut = Create(pubSub: false);
        var observer = new Observer();
        sut.Subscribe(observer);
        observer.Missed.Clear();
        Read();

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => observer.Missed.Count > 0);

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();
        observer.Missed.Should().Equal(MissedEventsReason.Lost);
        sut.IsSubscribed.Should().BeFalse();
        _streams.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task The_Disable_action_starts_on_Streams_and_turns_broadcast_off_when_the_server_refuses()
    {
        var sut = Create(StreamsUnsupportedAction.Disable);
        sut.OnStreams.Should().BeTrue();
        var observer = new Observer();
        sut.Subscribe(observer);
        Read();

        _capability.Refused(StreamsFakes.UnknownCommand());
        // The state is swapped before the observers are told, so waiting for the state alone races the Lost.
        await WaitForAsync(() => !sut.OnStreams && observer.Missed.Count > 0);

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();
        observer.Missed.Should().Equal(MissedEventsReason.Lost);
        _pubSub.Published.Should().BeEmpty();
        _streams.Disposed.Should().BeTrue();
        _fallback.Created.Should().Be(0);
    }

    [Fact]
    public async Task The_Disable_action_on_a_server_known_to_refuse_makes_no_Streams_topic()
    {
        _capability.Refused(StreamsFakes.UnknownCommand());
        var sut = Create(StreamsUnsupportedAction.Disable);

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();

        _pubSub.Published.Should().BeEmpty();
        _streams.Created.Should().Be(0);
        _fallback.Created.Should().Be(0);
    }

    [Fact]
    public async Task Pub_Sub_refused_as_well_turns_broadcast_off()
    {
        var sut = Create();
        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => _pubSub.Subscribers.Count == 1);

        _capability.PubSubRefused(StreamsFakes.NoPermission());
        await WaitForAsync(() => _pubSub.Subscribers.Count == 0);

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();
        _pubSub.Published.Should().BeEmpty();
        _pubSub.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task A_server_that_is_unsupported_from_the_start_goes_straight_to_the_fallback()
    {
        _capability.Refused(StreamsFakes.UnknownCommand());
        var sut = Create();

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();

        _streams.Created.Should().Be(0);
        _pubSub.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task IsSubscribed_follows_the_topic_beneath()
    {
        Hold();
        var sut = Create();
        sut.IsSubscribed.Should().BeFalse();
        _streams.Subscribed = true;
        sut.IsSubscribed.Should().BeTrue("the Streams topic says so");

        _capability.Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => !sut.OnStreams);
        sut.IsSubscribed.Should().BeFalse("the fallback says not");
        _pubSub.Subscribed = true;
        sut.IsSubscribed.Should().BeTrue();

        sut.Dispose();
        sut.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task Publishes_on_parallel_tasks_while_the_topic_switches_never_throw_and_all_land_once()
    {
        var sut = Create();
        const int Count = 1000;

        var results = await Task.WhenAll(Enumerable.Range(0, Count).Select(i => Task.Run(
            async () =>
            {
                if (i == Count / 4)
                {
                    _capability.Refused(StreamsFakes.UnknownCommand());
                }

                return await sut.PublishAsync(Event(null), Token);
            },
            Token)));

        results.Should().OnlyContain(published => published);
        await WaitForAsync(() => !sut.OnStreams);
        (_streams.PublishedCount + _pubSub.PublishedCount).Should().Be(Count);
    }

    [Fact]
    public async Task A_publish_on_a_Streams_topic_the_switch_just_disposed_goes_again_on_the_new_one()
    {
        var sut = Create();
        _streams.PublishThrows = new ObjectDisposedException("streams");
        _streams.OnPublish = () =>
        {
            _capability.Refused(StreamsFakes.UnknownCommand());
            _switches.Reader.ReadAsync(Token).AsTask().GetAwaiter().GetResult();
            sut.OnStreams.Should().BeFalse();
        };

        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue();

        _pubSub.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_publish_that_meets_the_dispose_of_the_topic_gives_false()
    {
        var sut = Create();
        _streams.PublishThrows = new ObjectDisposedException("streams");
        _streams.OnPublish = sut.Dispose;

        (await sut.PublishAsync(Event(null), Token)).Should().BeFalse();
    }

    [Fact]
    public async Task A_disposed_topic_rejects_publishes()
    {
        var sut = Create();
        sut.Dispose();

        await FluentActions.Awaiting(async () => await sut.PublishAsync(Event(null), Token)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Disposing_during_a_pending_probe_releases_the_Streams_topic_and_the_decision_changes_nothing()
    {
        var release = Hold();
        var sut = Create();

        sut.Dispose();
        release.SetResult(true);
        await _capability.WaitDecidedAsync(Wait, Token);
        _capability.Refused(StreamsFakes.UnknownCommand());

        _streams.Disposed.Should().BeTrue();
        _fallback.Created.Should().Be(0, "a disposed topic does not switch");
        _pubSub.Subscribers.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Streams_topic_is_disposed_when_the_bridge_cannot_attach_to_it()
    {
        _streams.Topic.Subscribe(Arg.Any<IObserver<ICacheEvent>>()).Throws(new InvalidOperationException("no subscription"));

        var sut = Create();

        _streams.Created.Should().Be(1);
        _streams.Disposed.Should().BeTrue("its fetch loop would run on with nobody to stop it");
        sut.OnStreams.Should().BeFalse();
        (await sut.PublishAsync(Event(null), Token)).Should().BeTrue("broadcast is off");
    }

    [Fact]
    public async Task Disposing_releases_the_Streams_topic_and_never_the_Pub_Sub_one()
    {
        var onStreams = Create();
        var disposed = 0;
        onStreams.OnDisposed += (_, _) => disposed++;

        onStreams.Dispose();
        onStreams.Dispose();

        _streams.Disposed.Should().BeTrue();
        disposed.Should().Be(1);
        _leftStreams.Should().BeGreaterThanOrEqualTo(1);
        FluentActions.Invoking(() => onStreams.Subscribe(new Observer())).Should().Throw<ObjectDisposedException>();

        var other = new FakeInner();
        _fallback.Topic = other.Topic;
        _capability.Refused(StreamsFakes.UnknownCommand());
        var onPubSub = Create();
        await onPubSub.PublishAsync(Event(null), Token);
        onPubSub.Dispose();

        other.Disposed.Should().BeFalse();
        other.Subscribers.Should().BeEmpty("the bridge is detached, and nothing else");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private static TestCacheEvent Event(string? key) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Source = new Uri("urn:test"),
        Data = key is null ? null : new CacheEventData(key),
    };

    private StreamsFallbackTopic Create(StreamsUnsupportedAction action = StreamsUnsupportedAction.PubSub, bool pubSub = true, BroadcastCapability? capability = null, ICachingTelemetryProvider? telemetry = null)
    {
        var recorded = (RecordingTelemetryProvider?)telemetry ?? new RecordingTelemetryProvider();
        recorded.EventRecorded += _ => _switches.Writer.TryWrite(true);
        var topic = new StreamsFallbackTopic(
            "orders",
            capability ?? _capability,
            StreamsFakes.Settings(action),
            pubSub ? _fallback : null,
            onRead => _streams.Make(onRead),
            new KeyedSubject<ICacheEvent>(NullLogger<KeyedSubject<ICacheEvent>>.Instance),
            NullLogger.Instance,
            onLeftStreams: () => Interlocked.Increment(ref _leftStreams),
            telemetry: recorded);
        _disposables.Add(topic);
        return topic;
    }

    // Created before the probe ran: it answers when released, or never.
    private TaskCompletionSource<bool> Hold()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).Returns(_ => release.Task);
        return release;
    }

    // The Streams topic read once, as the reader of a server that runs Streams does.
    private void Read() => _streams.OnRead!();

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await _switches.Reader.ReadAsync(Token);
        }
        condition().Should().BeTrue();
    }

    private sealed class RefusingTopic : ITopic<ICacheEvent>
    {
        event Action<Exception>? ITopic<ICacheEvent>.Refused
        {
            add => value?.Invoke(StreamsFakes.UnknownCommand());
            remove => _ = value;
        }

        public TopicKey TopicKey { get; } = new("orders");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => Substitute.For<IDisposable>();

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => ValueTask.FromResult(true);

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private sealed class FakeFallback : IPubSubFallback
    {
        public ITopic<ICacheEvent> Topic { get; set; } = default!;

        public int Created { get; private set; }

        public ITopic<ICacheEvent> Create(TopicKey topicKey)
        {
            Created++;
            return Topic;
        }
    }

    private sealed class FakeInner
    {
        public FakeInner()
        {
            Topic = Substitute.For<ITopic<ICacheEvent>, ITopicSubscriptionState>();
            Topic.Subscribe(Arg.Any<IObserver<ICacheEvent>>()).Returns(call =>
            {
                var observer = call.Arg<IObserver<ICacheEvent>>();
                Subscribers.Add(observer);
                return Disposable.Create((Subscribers, observer), static state => state.Subscribers.Remove(state.observer));
            });
            ValueTaskStubs.Returns(Topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()), call =>
            {
                ObjectDisposedException.ThrowIf(Disposed, "topic");

                lock (Published)
                {
                    Published.Add(call.Arg<ICacheEvent>());
                }

                OnPublish?.Invoke();
                return PublishThrows is { } error ? throw error : new ValueTask<bool>(PublishResult);
            });
            Topic.When(topic => topic.Dispose()).Do(_ => Disposed = true);
            ((ITopicSubscriptionState)Topic).IsSubscribed.Returns(_ => Subscribed);
        }

        public ITopic<ICacheEvent> Topic { get; }

        public List<IObserver<ICacheEvent>> Subscribers { get; } = [];

        public List<ICacheEvent> Published { get; } = [];

        public int PublishedCount
        {
            get
            {
                lock (Published)
                {
                    return Published.Count;
                }
            }
        }

        public bool PublishResult { get; set; } = true;

        public Exception? PublishThrows { get; set; }

        public Action? OnPublish { get; set; }

        public bool Subscribed { get; set; }

        public bool Disposed { get; private set; }

        public int Created { get; private set; }

        public Action? OnRead { get; private set; }

        public ITopic<ICacheEvent> Make(Action onRead)
        {
            Created++;
            OnRead = onRead;
            return Topic;
        }
    }

    private class Observer : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        private readonly List<string>? _order;
        private readonly string _name;

        public Observer(List<string>? order = null, string name = "")
        {
            _order = order;
            _name = name;
        }

        public List<ICacheEvent> Events { get; } = [];

        public List<MissedEventsReason> Missed { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(ICacheEvent value)
        {
            Events.Add(value);
            _order?.Add(_name);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => Completed = true;

        public void OnEventsMissed(MissedEventsReason reason) => Missed.Add(reason);
    }

    private sealed class KeyedObserver(string key, List<string>? order = null, string name = "") : Observer(order, name), IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = key;
    }

    private sealed class FirstObserver(List<string>? order = null, string name = "") : Observer(order, name), IFirstObserver<ICacheEvent>
    {
    }
}
