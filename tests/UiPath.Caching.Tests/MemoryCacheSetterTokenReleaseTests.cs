using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>
/// A local entry's change token must leave the topic as soon as the entry is gone, before the write that displaced it
/// returns. Left to the eviction callback, which runs on the thread pool, a rewritten key piles up dead tokens that
/// every event fans out to, and that the pool only drains once the writers let it.
/// </summary>
public class MemoryCacheSetterTokenReleaseTests
{
    private const string Key = "hot";

    private readonly CountingTopic _topic = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public void A_rewritten_key_keeps_one_subscription()
    {
        var sut = Build(new InMemoryCacheOptions());

        for (var i = 0; i < 100; i++)
        {
            Set(sut, i).Should().BeTrue();
            _topic.Live.Should().Be(1, "the replaced entry's token is detached by the cache before Set returns");
        }
    }

    [Fact]
    public void A_removed_key_keeps_no_subscription()
    {
        var sut = Build(new InMemoryCacheOptions());
        Set(sut, 1).Should().BeTrue();

        _cache.Remove(Key);

        _topic.Live.Should().Be(0);
    }

    [Fact]
    public void An_entry_the_cache_did_not_keep_leaves_no_subscription()
    {
        // What the memory cache does to an entry that lost the commit to a concurrent write of the same key, or expired on insert: it never attaches the token, so only the eviction callback, queued on the thread pool, would release it.
        var entry = Substitute.For<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        entry.ExpirationTokens.Returns([]);
        entry.PostEvictionCallbacks.Returns([]);
        var cache = Substitute.For<IMemoryCache>();
        cache.CreateEntry(Key).Returns(entry);
        var sut = Build(new InMemoryCacheOptions(), cache);

        Set(sut, 1).Should().BeTrue();

        _topic.Live.Should().Be(0, "the setter releases a token the cache did not keep, before the write returns");
    }

    [Fact]
    public void A_write_that_throws_leaves_no_subscription()
    {
        var sut = Build(new InMemoryCacheOptions { SizeLimit = 1, SizeProvider = new ThrowingSizeProvider() });

        Set(sut, 1).Should().BeFalse();

        _topic.Live.Should().Be(0);
    }

    private static bool Set(LocalMemorySetter sut, int value) =>
        sut.Set(
            new CacheEntryOptions { CacheKey = Key, TopicKey = "t", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) },
            new TestCacheEntry<int> { Value = value },
            typeof(int),
            maxExpiration: null);

    private LocalMemorySetter Build(InMemoryCacheOptions options, IMemoryCache? cache = null)
    {
        var tokens = new ChangeTokenFactory<byte[]>(Options.Create(new CacheOptions()), Substitute.For<ISerializerProxy<byte[]>>(), NullLoggerFactory.Instance, NullTelemetryProvider.Instance);
        return new LocalMemorySetter(KnownCacheProviderNames.InMemoryRedis, tokens, new SingleTopicProvider(_topic), cache ?? _cache, NullLogger.Instance, TimeProvider.System, options, options, NullTelemetryProvider.Instance);
    }

    private sealed class ThrowingSizeProvider : ICacheEntrySizeProvider
    {
        public long GetSize(ICacheEntry entry) => throw new InvalidOperationException("no size");
    }

    /// <summary>Counts the subscriptions still open: what the topic's subject would hold for the key.</summary>
    private sealed class CountingTopic : ITopic<ICacheEvent>
    {
        private int _live;

        public int Live => Volatile.Read(ref _live);

        public TopicKey TopicKey => "t";

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            Interlocked.Increment(ref _live);
            return new Subscription(this);
        }

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => new(true);

        public void Dispose()
        {
        }

        private sealed class Subscription(CountingTopic topic) : IDisposable
        {
            private CountingTopic? _topic = topic;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _topic, null) is { } topic)
                {
                    Interlocked.Decrement(ref topic._live);
                }
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
