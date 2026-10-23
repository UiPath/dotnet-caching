#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Every set that announces itself writes the value first and publishes second, and announces what the inner tier holds.</summary>
public class MultilayerSetOrderTests
{
    private static readonly IDictionary<string, string?> Fields = new Dictionary<string, string?> { ["f"] = "v" };

    private static readonly CachePolicy Rehydrating = new()
    {
        DistributedExpiration = TimeSpan.FromMinutes(10),
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions { Threshold = 0.75, BaseCooldown = TimeSpan.FromSeconds(1), MaxCooldown = TimeSpan.FromMinutes(5), TimeoutFraction = 0.5, Name = "t" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hash_set_writes_before_it_publishes()
    {
        using var rig = new Rig();

        (await rig.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeTrue();

        rig.Log.Should().Equal("write:k", "event:k");
        rig.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_hash_set_with_options_writes_before_it_publishes()
    {
        using var rig = new Rig();

        (await rig.Hash.SetAsync<string>("k", Fields, new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), policy: null, Ct)).Should().BeTrue();

        rig.Log.Should().Equal("write:k", "event:k");
    }

    [Fact]
    public async Task A_hash_set_the_inner_tier_refuses_publishes_nothing()
    {
        using var rig = new Rig { WriteResult = false };

        (await rig.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:k");
    }

    [Fact]
    public async Task A_hash_set_whose_write_throws_publishes_nothing()
    {
        using var rig = new Rig { WriteThrows = true };

        (await rig.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:k");
    }

    [Fact]
    public async Task A_hash_set_whose_event_is_refused_or_throws_returns_false_with_the_write_in_place()
    {
        using var refused = new Rig { PublishResult = false };
        using var thrown = new Rig { PublishThrows = true };

        (await refused.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeFalse();
        (await thrown.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeFalse();

        refused.Log.Should().Equal("write:k", "event:k");
        thrown.Log.Should().Equal("write:k", "event:k");
    }

    [Fact]
    public async Task A_hash_set_the_inner_tier_took_is_published_when_the_local_tier_fails()
    {
        using var rig = new Rig(failingMemory: true);

        (await rig.Hash.SetAsync<string>("k", Fields, policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:k", "event:k");
    }

    [Fact]
    public async Task A_rehydrated_value_is_written_before_it_is_published()
    {
        using var rig = new Rig();
        rig.InnerEntry("k");

        (await rig.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("fresh"), Rehydrating, Ct)).Should().Be("old");

        await rig.UntilAsync(() => rig.Log.Contains("event:k"));
        rig.Log.Should().Equal("write:k", "event:k");
        rig.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_rehydrated_value_the_inner_tier_refuses_is_not_published()
    {
        using var rig = new Rig { WriteResult = false };
        rig.InnerEntry("k");

        await rig.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("fresh"), Rehydrating, Ct);

        await rig.UntilAsync(() => rig.Log.Contains("write:k"));
        await Task.Delay(100, Ct);
        rig.Log.Should().Equal("write:k");
    }

    [Fact]
    public async Task A_rehydrated_hash_is_written_before_it_is_published()
    {
        using var rig = new Rig();
        rig.InnerHashEntry("k");

        await rig.Hash.GetOrAddAsync<string>("k", _ => Task.FromResult(Fields), Rehydrating, Ct);

        await rig.UntilAsync(() => rig.Log.Contains("event:k"));
        rig.Log.Should().Equal("write:k", "event:k");
        rig.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_rehydrated_hash_the_inner_tier_refuses_is_not_published()
    {
        using var rig = new Rig { WriteResult = false };
        rig.InnerHashEntry("k");

        await rig.Hash.GetOrAddAsync<string>("k", _ => Task.FromResult(Fields), Rehydrating, Ct);

        await rig.UntilAsync(() => rig.Log.Contains("write:k"));
        await Task.Delay(100, Ct);
        rig.Log.Should().Equal("write:k");
    }

    [Fact]
    public async Task A_batch_set_publishes_every_key_the_inner_tier_holds_when_only_the_local_tier_fails()
    {
        using var rig = new Rig(failingMemory: true);

        (await rig.Cache.SetAsync<string>([new("a", "1"), new("b", "2")], policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:a,b", "event:a", "event:b");
    }

    [Fact]
    public async Task A_batch_set_whose_first_event_is_refused_still_publishes_the_rest_and_returns_false()
    {
        using var rig = new Rig { PublishResultFor = key => key != "a" };

        (await rig.Cache.SetAsync<string>([new("a", "1"), new("b", "2")], policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:a,b", "event:a", "event:b");
    }

    [Fact]
    public async Task A_batch_set_the_inner_tier_refuses_still_announces_every_key_after_the_write()
    {
        // Across Redis Cluster slots a refused batch can have written some of its keys.
        using var rig = new Rig { WriteResult = false };

        (await rig.Cache.SetAsync<string>([new("a", "1"), new("b", "2")], policy: null, Ct)).Should().BeFalse();

        rig.Log.Should().Equal("write:a,b", "event:a", "event:b");
    }

    [Fact]
    public async Task A_batch_set_publishes_after_it_writes()
    {
        using var rig = new Rig();

        (await rig.Cache.SetAsync<string>([new("a", "1"), new("b", "2")], policy: null, Ct)).Should().BeTrue();

        rig.Log.Should().Equal("write:a,b", "event:a", "event:b");
    }

    private sealed class RecordingTopic(Rig rig) : ITopic<ICacheEvent>
    {
        public TopicKey TopicKey { get; } = new("topic");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => new Nothing();

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => rig.Publish(@event);

        public void Dispose()
        {
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly object _gate = new();

        public Rig(bool failingMemory = false)
        {
            var options = new InMemoryRedisCacheOptions();
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(new RecordingTopic(this));
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            var locks = Substitute.For<IDistributedLock>();
            locks.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
            IMemoryCacheFactory memory = failingMemory ? new ThrowingMemoryCacheFactory() : new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance);
            var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false };

            InnerCache = Substitute.For<ICache>();
            ValueTaskStubs.Returns(
                InnerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"write:{string.Join(',', call.ArgAt<KeyValuePair<CacheKey, string?>[]>(0).Select(p => p.Key.Name))}");
                    return new ValueTask<bool>(WriteResult);
                });
            ValueTaskStubs.Returns(
                InnerCache.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"write:{call.ArgAt<CacheKey>(0).Name}");
                    return new ValueTask<bool>(WriteResult);
                });
            Cache = RunTables.Track(new MultilayerCache(KnownCacheProviderNames.InMemoryRedis, InnerCache, memory, NullChangeTokenFactory.Instance, topics, events, NullTelemetryProvider.Instance, options, options, cacheOptions, locks, NullCachePolicyFactory.Instance, TimeProvider.System, NullLogger.Instance));

            InnerHash = Substitute.For<IHashCache>();
            ValueTaskStubs.Returns(
                InnerHash.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"write:{call.ArgAt<CacheKey>(0).Name}");
                    return WriteThrows ? throw new InvalidOperationException("redis is down") : new ValueTask<bool>(WriteResult);
                });
            Hash = RunTables.Track(new MultilayerHashCache(KnownCacheProviderNames.InMemoryRedis, InnerHash, memory, NullChangeTokenFactory.Instance, topics, events, NullTelemetryProvider.Instance, options, options, cacheOptions, locks, NullCachePolicyFactory.Instance, TimeProvider.System, NullLogger.Instance));
        }

        public MultilayerCache Cache { get; }

        public MultilayerHashCache Hash { get; }

        public ICache InnerCache { get; }

        public IHashCache InnerHash { get; }

        public List<string> Log { get; } = [];

        public List<bool> WrittenWhenPublished { get; } = [];

        public bool WriteResult { get; set; } = true;

        public bool WriteThrows { get; set; }

        public bool PublishResult { get; set; } = true;

        public bool PublishThrows { get; set; }

        public Func<string, bool>? PublishResultFor { get; set; }

        public void InnerEntry(string key) =>
            ValueTaskStubs.Returns(
                InnerCache.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "old", Expiration = DateTimeOffset.UtcNow.AddMinutes(2) }));

        public void InnerHashEntry(string key) =>
            ValueTaskStubs.Returns(
                InnerHash.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Value = new Dictionary<string, string?> { ["f"] = "old" }, Expiration = DateTimeOffset.UtcNow.AddMinutes(2) }));

        public ValueTask<bool> Publish(ICacheEvent @event)
        {
            var key = @event.Data?.Key ?? string.Empty;
            lock (_gate)
            {
                WrittenWhenPublished.Add(Log.Count > 0 && Log[^1].StartsWith("write:", StringComparison.Ordinal));
                Log.Add($"event:{key}");
            }

            if (PublishThrows)
            {
                throw new InvalidOperationException("stream is gone");
            }

            return new ValueTask<bool>(PublishResultFor?.Invoke(key) ?? PublishResult);
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                (DateTime.UtcNow < deadline).Should().BeTrue("the condition was not met in time: {0}", string.Join(", ", Log));
                await Task.Delay(5, Ct);
            }
        }

        public void Dispose()
        {
            Cache.Dispose();
            Hash.Dispose();
        }

        private void Add(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
            }
        }
    }

    private sealed class ThrowingMemoryCacheFactory : IMemoryCacheFactory
    {
        public IMemoryCache Get(IMemoryCacheOptions memoryOptions) => new ThrowingMemoryCache();
    }

    private sealed class ThrowingMemoryCache : IMemoryCache
    {
        public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key) => throw new InvalidOperationException("the local tier is broken");

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public void Remove(object key)
        {
        }

        public void Dispose()
        {
        }
    }
}
