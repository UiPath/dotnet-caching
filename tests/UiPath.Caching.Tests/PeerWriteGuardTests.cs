using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests;

public class PeerWriteGuardTests
{
    private const string Key = "k";
    private static readonly TopicKey Topic = new("topic");

    private readonly BackgroundWrites _writes = new(0, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
    private readonly SteppedClock _clock = new();
    private readonly ITopicProvider _topics = Substitute.For<ITopicProvider>();
    private FakeTopic _current = new();

    public PeerWriteGuardTests() => _topics.Create(Arg.Any<TopicKey>()).Returns(_ => _current);

    [Fact]
    public void Watching_counts_changes_for_fills_without_ordering_writes()
    {
        using var guard = Guard();

        guard.Watch(Topic);

        _writes.Counting.Should().BeTrue();
        _writes.Active.Should().BeFalse();
    }

    [Fact]
    public void A_topic_that_completes_refuses_fills_in_flight_and_is_watched_again_once_made_again()
    {
        using var guard = Guard();
        var first = _current;
        guard.Watch(Topic);
        var mark = _writes.Mark(Key);

        first.Subject.OnCompleted();
        Refused(mark).Should().BeTrue("events published on the topic made again are not heard until the next read watches it");

        _current = new FakeTopic();
        guard.Watch(Topic);
        _current.Subscribes.Should().Be(1);
        mark = _writes.Mark(Key);
        _current.Subject.OnNext(Event(KnownEventTypes.CacheSet));
        Refused(mark).Should().BeTrue();
    }

    [Fact]
    public void A_failed_watch_is_tried_again_only_after_a_delay()
    {
        using var guard = Guard();
        _current.Throws = true;

        for (var i = 0; i < 5; i++)
        {
            guard.Watch(Topic);
        }

        _current.Subscribes.Should().Be(1);
        _current.Throws = false;
        _clock.Advance(PeerWriteGuard.RetryDelay);
        guard.Watch(Topic);
        guard.Watch(Topic);

        _current.Subscribes.Should().Be(2);
        var mark = _writes.Mark(Key);
        _current.Subject.OnNext(Event(KnownEventTypes.CacheSet));
        Refused(mark).Should().BeTrue();
    }

    [Theory]
    [InlineData(KnownEventTypes.CacheSet, false, true)]
    [InlineData(KnownEventTypes.CacheRemoved, false, true)]
    [InlineData(KnownEventTypes.CacheRefreshed, false, false)]
    [InlineData(KnownEventTypes.CacheRefreshed, true, true)]
    public void A_peer_event_refuses_a_fill_when_it_may_change_what_the_read_found(string type, bool metadata, bool refused)
    {
        using var guard = Guard();
        guard.Watch(Topic);
        var mark = _writes.Mark(Key);

        _current.Subject.OnNext(Event(type, metadata: metadata));

        Refused(mark).Should().Be(refused);
    }

    [Fact]
    public void This_node_s_own_events_are_ignored()
    {
        using var guard = Guard();
        guard.Watch(Topic);
        var mark = _writes.Mark(Key);

        _current.Subject.OnNext(Event(KnownEventTypes.CacheSet, new Uri("URN:NODE:B")));

        Refused(mark).Should().BeFalse();
    }

    [Fact]
    public void Disposing_ends_the_subscriptions()
    {
        var guard = Guard();
        guard.Watch(Topic);

        guard.Dispose();
        var mark = _writes.Mark(Key);
        _current.Subject.OnNext(Event(KnownEventTypes.CacheSet));

        Refused(mark).Should().BeFalse();
        guard.Watch(Topic);
        _current.Subscribes.Should().Be(1);
    }

    private static TestCacheEvent Event(string type, Uri? source = null, bool metadata = false) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Source = source ?? new Uri("urn:node:a"),
        Data = new CacheEventData(Key)
        {
            Properties = metadata ? new Dictionary<string, object?> { [KnownFieldNames.MetadataKey] = null } : new Dictionary<string, object?> { [KnownFieldNames.ExpirationKey] = DateTimeOffset.UtcNow },
        },
    };

    private PeerWriteGuard Guard() => new(_writes, _topics, new Uri("urn:node:b"), _clock, NullLogger.Instance);

    private bool Refused(long mark) => _writes.Fill(Key, mark, 0, static _ => true) is null;

    private sealed class SteppedClock : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class FakeTopic : ITopic<ICacheEvent>
    {
        private int _subscribes;

        public KeyedSubject<ICacheEvent> Subject { get; } = new(NullLogger.Instance);

        public bool Throws { get; set; }

        public int Subscribes => Volatile.Read(ref _subscribes);

        public TopicKey TopicKey => Topic;

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            Interlocked.Increment(ref _subscribes);
            return Throws ? throw new InvalidOperationException("subscribe failed") : Subject.Subscribe(observer);
        }

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => new(true);

        public void Dispose()
        {
        }
    }
}
