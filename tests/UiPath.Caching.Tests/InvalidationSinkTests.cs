using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests;

public sealed class InvalidationSinkTests : IDisposable
{
    private const string Key = "hot";
    private const string ThisNode = "urn:this-node";

    private readonly CapturingTopic _topic = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly RecordingSink _sink;
    private readonly SingleTopicProvider _topics;

    public InvalidationSinkTests()
    {
        _topics = new SingleTopicProvider(_topic);
        // A token that changed reads as expired, so a live entry at the report means the key was guarded before any reader could miss it.
        _sink = new RecordingSink(() => _cache.TryGetValue(Key, out _));
    }

    public void Dispose()
    {
        _topics.Dispose();
        _topic.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public void A_peers_removal_names_the_key()
    {
        Set(Build());

        _topic.Raise(Event("urn:peer", Key));

        _sink.Keys.Should().Equal(Key);
        _sink.All.Should().Be(0);
    }

    [Fact]
    public void An_event_for_another_key_or_from_this_node_names_nothing()
    {
        Set(Build());

        _topic.Raise(Event("urn:peer", "other"));
        _topic.Raise(Event(ThisNode, Key));

        _sink.Keys.Should().BeEmpty();
        _sink.All.Should().Be(0);
    }

    [Theory]
    [InlineData(MissedEventsReason.Lost)]
    [InlineData(MissedEventsReason.SubscriptionGap)]
    public void Lost_events_name_no_key_and_guard_everything(MissedEventsReason reason)
    {
        Set(Build());

        _topic.Miss(reason);

        _sink.All.Should().Be(1);
        _sink.Keys.Should().BeEmpty();
    }

    [Fact]
    public void A_topic_error_guards_everything()
    {
        Set(Build());

        _topic.Fail();

        _sink.All.Should().Be(1);
        _sink.Keys.Should().BeEmpty();
    }

    [Theory]
    [InlineData("event")]
    [InlineData("loss")]
    [InlineData("error")]
    public void The_key_is_reported_before_the_local_entry_reads_as_expired(string cause)
    {
        Set(Build());

        switch (cause)
        {
            case "event":
                _topic.Raise(Event("urn:peer", Key));
                break;
            case "loss":
                _topic.Miss(MissedEventsReason.Lost);
                break;
            default:
                _topic.Fail();
                break;
        }

        _sink.LiveAtReport.Should().Equal(true);
        _cache.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void A_peer_update_the_entry_applied_still_names_the_key()
    {
        var options = new InMemoryRedisCacheOptions();
        var tokens = new ChangeTokenFactory<byte[]>(Options.Create(new CacheOptions { SourceUri = new Uri(ThisNode) }), new SystemJsonByteSerializerProxy(), NullLoggerFactory.Instance, NullTelemetryProvider.Instance);
        var sut = new HashLocalMemorySetter(KnownCacheProviderNames.InMemoryRedis, tokens, _topics, _cache, NullLogger.Instance, TimeProvider.System, options, options, NullTelemetryProvider.Instance)
        {
            Invalidations = _sink,
            PeerApply = (_, _) => true,
        };
        sut.Set(
            new CacheEntryOptions { CacheKey = Key, TopicKey = "t", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) },
            new TestCacheEntry<int> { Value = 1 },
            typeof(int),
            maxExpiration: null).Should().BeTrue();

        _topic.Raise(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:peer"),
            Data = new CacheEventData(Key, new Dictionary<string, object?>
            {
                [KnownFieldNames.VersionKey] = 2L,
                [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["a"] = "1" },
            }),
            Type = "set",
        });

        _sink.Keys.Should().Equal(Key);
        _cache.TryGetValue(Key, out _).Should().BeTrue("the update was applied, so the entry stays");
    }

    [Fact]
    public void Nothing_is_reported_without_a_sink()
    {
        Set(Build(withSink: false));

        _topic.Raise(Event("urn:peer", Key));

        _sink.Keys.Should().BeEmpty();
    }

    private static TestCacheEvent Event(string source, string key) =>
        new() { Id = Guid.NewGuid().ToString(), Source = new Uri(source), Data = new CacheEventData(key), Type = "removed" };

    private static void Set(LocalMemorySetter sut) =>
        sut.Set(
            new CacheEntryOptions { CacheKey = Key, TopicKey = "t", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) },
            new TestCacheEntry<int> { Value = 1 },
            typeof(int),
            maxExpiration: null).Should().BeTrue();

    private LocalMemorySetter Build(bool withSink = true)
    {
        var options = new InMemoryCacheOptions();
        var tokens = new ChangeTokenFactory<byte[]>(Options.Create(new CacheOptions { SourceUri = new Uri(ThisNode) }), Substitute.For<ISerializerProxy<byte[]>>(), NullLoggerFactory.Instance, NullTelemetryProvider.Instance);
        return new LocalMemorySetter(KnownCacheProviderNames.InMemoryRedis, tokens, _topics, _cache, NullLogger.Instance, TimeProvider.System, options, options, NullTelemetryProvider.Instance)
        {
            Invalidations = withSink ? _sink : null,
        };
    }

    private sealed class RecordingSink(Func<bool> live) : IInvalidationSink
    {
        public List<string> Keys { get; } = [];

        public List<bool> LiveAtReport { get; } = [];

        public int All { get; private set; }

        public void Invalidated(CacheKey key)
        {
            LiveAtReport.Add(live());
            Keys.Add(key.Name);
        }

        public void InvalidatedAll()
        {
            LiveAtReport.Add(live());
            All++;
        }
    }

    private sealed class CapturingTopic : ITopic<ICacheEvent>
    {
        private IObserver<ICacheEvent>? _observer;

        public TopicKey TopicKey => "t";

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            _observer = observer;
            return new Subscription();
        }

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => new(true);

        public void Raise(ICacheEvent @event) => _observer!.OnNext(@event);

        public void Miss(MissedEventsReason reason) => ((IMissedEventsObserver)_observer!).OnEventsMissed(reason);

        public void Fail() => _observer!.OnError(new InvalidOperationException("topic failed"));

        public void Dispose()
        {
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class SingleTopicProvider(ITopic<ICacheEvent> topic) : ITopicProvider
    {
        public string Name => "test";

        public bool Enabled => true;

        public ICollection<TopicKey> Keys => [topic.TopicKey];

        public ITopic<ICacheEvent> Create(TopicKey topicKey) => topic;

        public void Remove(TopicKey topicKey)
        {
        }

        public void Dispose()
        {
        }
    }
}
