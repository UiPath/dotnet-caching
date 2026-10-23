#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class MultilayerHashCacheManyTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly HashCacheEntryOptions Replace = new(SetOption: HashCacheSetOption.KeyReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task RemoveMany_covers_every_hash_across_chunk_boundaries(int count)
    {
        using var h = new Harness(chunk: 4);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(count), Ct);

        failed.Should().BeEmpty();
        h.Log.Where(l => l.StartsWith("remove:", StringComparison.Ordinal)).Should().Equal(Keys(count).Select(k => $"remove:{k.Name}"));
        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal(Keys(count).Select(k => $"event:CacheRemoved:{k.Name}"));
    }

    [Fact]
    public async Task RemoveMany_starts_every_delete_of_a_chunk_before_it_awaits_any_and_then_the_events()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnRemove = _ => new ValueTask<bool>(gate.Task);

        var pending = h.Cache.RemoveManyAsync<string>(Keys(4), Ct).AsTask();

        await h.UntilAsync(() => h.Count("remove:") == 4);
        pending.IsCompleted.Should().BeFalse();
        h.Count("event:").Should().Be(0);
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveMany_reports_the_failed_hash_of_a_middle_chunk_and_carries_on()
    {
        using var h = new Harness(chunk: 4);
        h.OnRemove = key => key == "k5" ? new ValueTask<bool>(false) : key == "k6" ? throw new InvalidOperationException("down") : new ValueTask<bool>(true);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(12), Ct);

        failed.Select(k => k.Name).Should().Equal("k5", "k6");
        h.Count("remove:").Should().Be(12);
        h.Log.Should().Contain("event:CacheRemoved:k5");
    }

    [Fact]
    public async Task RemoveMany_stops_issuing_when_cancelled()
    {
        using var h = new Harness(chunk: 4);
        using var cancel = new CancellationTokenSource();
        h.OnRemove = key =>
        {
            if (key == "k1")
            {
                cancel.Cancel();
            }

            return new ValueTask<bool>(true);
        };

        var act = async () => await h.Cache.RemoveManyAsync<string>(Keys(12), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Count("remove:").Should().Be(2, "no delete is issued after the cancel, in the chunk or a later one");
    }

    [Fact]
    public async Task RemoveMany_waits_for_a_pending_background_write_of_a_hash()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnWrite = key => key == "k1" ? gate.Task : Task.FromResult(true);
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };
        await h.Cache.SetAsync<string>("k1", new Dictionary<string, string?> { ["f"] = "v" }, background, Ct);

        var pending = h.Cache.RemoveManyAsync<string>(Keys(3), Ct).AsTask();

        await Task.Delay(100, Ct);
        pending.IsCompleted.Should().BeFalse();
        h.Count("remove:").Should().Be(0);
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SetMany_writes_before_the_events_and_keeps_the_hashes_locally(int count)
    {
        using var h = new Harness(chunk: 4);

        var failed = await h.Cache.SetManyAsync<string>(Pairs(count), Replace, Awaited, Ct);

        failed.Should().BeEmpty();
        h.Count("write:").Should().Be(count);
        h.Count("event:").Should().Be(count);
        h.Log.Take(Math.Min(4, count)).Should().OnlyContain(l => l.StartsWith("write:", StringComparison.Ordinal));
        h.Local("k0").Should().Be("v-k0");
    }

    [Fact]
    public async Task SetMany_gives_a_hash_whose_write_failed_no_event_and_reports_it()
    {
        using var h = new Harness(chunk: 4);
        h.OnWrite = key => key == "k5" ? Task.FromResult(false) : key == "k6" ? throw new InvalidOperationException("down") : Task.FromResult(true);

        var failed = await h.Cache.SetManyAsync<string>(Pairs(12), Replace, Awaited, Ct);

        failed.Select(k => k.Name).Should().Equal("k5", "k6");
        h.Log.Should().NotContain("event:CacheSet:k5").And.NotContain("event:CacheSet:k6");
        h.Log.Should().Contain("event:CacheSet:k11");
    }

    [Fact]
    public async Task SetMany_reports_a_hash_whose_event_failed()
    {
        using var h = new Harness(chunk: 4);
        h.OnPublish = e => new ValueTask<bool>(e.Key != "k1");

        var failed = await h.Cache.SetManyAsync<string>(Pairs(3), Replace, Awaited, Ct);

        failed.Select(k => k.Name).Should().Equal("k1");
    }

    [Fact]
    public async Task SetMany_is_awaited_even_when_the_policy_asks_for_background_writes()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnWrite = _ => gate.Task;
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };

        var pending = h.Cache.SetManyAsync<string>(Pairs(2), Replace, background, Ct).AsTask();

        await h.UntilAsync(() => h.Count("write:") == 2);
        pending.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
    }

    [Fact]
    public async Task SetMany_of_an_empty_hash_removes_the_key()
    {
        using var h = new Harness(chunk: 4);
        var pairs = new[] { new KeyValuePair<CacheKey, IDictionary<string, string?>>("a", new Dictionary<string, string?>()) };

        (await h.Cache.SetManyAsync<string>(pairs, Replace, Awaited, Ct)).Should().BeEmpty();

        h.Log.Should().Contain("remove:a").And.Contain("event:CacheRemoved:a");
    }

    [Fact]
    public async Task A_known_size_above_the_threshold_publishes_one_topic_wide_event_in_place_of_one_per_hash()
    {
        using var h = new Harness(chunk: 4, threshold: 5);

        (await h.Cache.SetManyAsync<string>(Pairs(10), Replace, Awaited, Ct)).Should().BeEmpty();
        (await h.Cache.RemoveManyAsync<string>(Keys(10), Ct)).Should().BeEmpty();

        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal($"event:{KnownEventTypes.TopicInvalidated}:*", $"event:{KnownEventTypes.TopicInvalidated}:*");
        h.Telemetry.Received(2).TrackEvent("cache.batch.topic_invalidation", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_stream_of_unknown_size_keeps_one_event_per_hash()
    {
        using var h = new Harness(chunk: 4, threshold: 2);

        await h.Cache.RemoveManyAsync<string>(Keys(10).Where(_ => true), Ct);

        h.Count("event:").Should().Be(10);
    }

    [Fact]
    public async Task A_topic_wide_event_that_fails_sends_every_key_back()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        h.OnPublish = _ => new ValueTask<bool>(false);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(10), Ct);

        failed.Select(k => k.Name).Should().Equal(Keys(10).Select(k => k.Name));
    }

    [Fact]
    public async Task RemoveFields_drops_the_whole_local_hash_removes_the_fields_and_announces_the_key_as_removed()
    {
        using var h = new Harness(chunk: 4);
        await h.Cache.SetAsync<string>("k", new Dictionary<string, string?> { ["f"] = "1", ["g"] = "2" }, Awaited, Ct);
        h.Local("k").Should().Be("1");

        (await h.Cache.RemoveFieldsAsync<string>("k", ["g"], Ct)).Should().BeTrue();

        h.Local("k").Should().BeNull("the local hash is replaced whole, never patched");
        h.Log.SkipWhile(l => !l.StartsWith("fields:", StringComparison.Ordinal)).Should().Equal("fields:k:g", "event:CacheRemoved:k");
    }

    [Fact]
    public async Task RemoveFields_returns_false_when_the_removal_failed_and_still_announces_it()
    {
        using var h = new Harness(chunk: 4);
        h.OnRemoveFields = _ => new ValueTask<bool>(false);

        (await h.Cache.RemoveFieldsAsync<string>("k", ["g"], Ct)).Should().BeFalse();

        h.Log.Should().Contain("event:CacheRemoved:k");
    }

    [Fact]
    public async Task RemoveFields_with_no_fields_does_nothing()
    {
        using var h = new Harness(chunk: 4);

        (await h.Cache.RemoveFieldsAsync<string>("k", [], Ct)).Should().BeTrue();

        h.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFields_waits_for_a_pending_background_write_of_the_hash()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnWrite = _ => gate.Task;
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };
        await h.Cache.SetAsync<string>("k", new Dictionary<string, string?> { ["f"] = "v" }, background, Ct);

        var pending = h.Cache.RemoveFieldsAsync<string>("k", ["f"], Ct).AsTask();

        await Task.Delay(100, Ct);
        pending.IsCompleted.Should().BeFalse();
        h.Count("fields:").Should().Be(0);
        gate.SetResult(true);
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveFields_on_a_hash_cache_that_cannot_throws_instead_of_pretending()
    {
        var cache = Substitute.For<IHashCache>();

        var act = async () => await cache.RemoveFieldsAsync<string>("k", ["f"], Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task The_fallback_on_a_hash_cache_without_the_capability_calls_the_single_key_members()
    {
        var cache = Substitute.For<IHashCache>();
        ValueTaskStubs.Returns(cache.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()), call => new ValueTask<bool>(call.ArgAt<CacheKey>(0).Name != "k1"));
        ValueTaskStubs.Returns(cache.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), call => new ValueTask<bool>(call.ArgAt<CacheKey>(0).Name != "k2"));

        var removed = await cache.RemoveManyAsync<string>(Keys(3), Ct);
        var set = await cache.SetManyAsync<string>(Pairs(3), Replace, null, Ct);

        removed.Select(k => k.Name).Should().Equal("k1");
        set.Select(k => k.Name).Should().Equal("k2");
    }

    [Fact]
    public async Task Hash_cache_of_T_gives_back_the_callers_own_keys_whatever_the_strategy_composed()
    {
        var inner = Substitute.For<IHashCache, ISpanKeyHashCache>();
        var multi = (ISpanKeyHashCache)inner;
        ValueTaskStubs.Returns(
            multi.RemoveManyAsync<string>(Arg.Any<IEnumerable<CacheKey>>(), Arg.Any<CancellationToken>()),
            call =>
            {
                var seen = call.ArgAt<IEnumerable<CacheKey>>(0).ToList();
                seen.Select(k => k.Name).Should().OnlyContain(n => n.StartsWith("p:", StringComparison.Ordinal));
                return new ValueTask<IReadOnlyList<CacheKey>>(seen.Where(k => k.Name.EndsWith('2')).ToList());
            });
        var cache = new HashCache<string>(inner, new PrefixCacheKeyStrategy("p"));

        (await cache.RemoveManyAsync(Keys(4), Ct)).Select(k => k.Name).Should().Equal("k2");
        (await cache.RemoveManyAsync(Keys(4).Where(_ => true), Ct)).Select(k => k.Name).Should().Equal("k2");
    }

    [Theory]
    [InlineData(false, "w", "")]
    [InlineData(false, "", "w")]
    [InlineData(false, "w1", "", "w2")]
    [InlineData(true, "w", "")]
    [InlineData(true, "", "w")]
    [InlineData(true, "w1", "", "w2")]
    public async Task SetMany_merging_a_key_twice_ends_as_the_same_merges_one_by_one(bool cacheNullValues, params string[] writes)
    {
        var merge = new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace);
        var values = writes.Select(w => (IDictionary<string, string?>)(w.Length == 0 ? [] : new Dictionary<string, string?> { [w] = w })).ToArray();
        using var single = new Harness(chunk: 8, cacheNullValues: cacheNullValues);
        using var many = new Harness(chunk: 8, cacheNullValues: cacheNullValues);
        foreach (var value in values)
        {
            await single.Cache.SetAsync<string>("a", value, merge, Awaited, Ct);
        }

        (await many.Cache.SetManyAsync<string>(values.Select(v => new KeyValuePair<CacheKey, IDictionary<string, string?>>("a", v)), merge, Awaited, Ct)).Should().BeEmpty();

        many.L2.Should().BeEquivalentTo(single.L2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("_meta_")]
    [InlineData(null)]
    public async Task RemoveFields_of_a_field_no_tier_accepts_throws_before_it_drops_the_local_hash_or_tells_the_others(string? field)
    {
        using var h = new Harness();
        (await h.Cache.SetAsync<string>("a", new Dictionary<string, string?> { ["f"] = "1" }, Replace, Awaited, Ct)).Should().BeTrue();
        h.Log.Clear();

        var act = async () => await h.Cache.RemoveFieldsAsync<string>("a", [field!], Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        h.Log.Should().BeEmpty();
        (await h.Cache.GetAsync<string>("a", Awaited, Ct)).Should().ContainKey("f", "the local hash stays");
    }

    private static CacheKey[] Keys(int count) => Enumerable.Range(0, count).Select(i => (CacheKey)$"k{i}").ToArray();

    private static KeyValuePair<CacheKey, IDictionary<string, string?>>[] Pairs(int count, string value = "v") =>
        Keys(count).Select(k => new KeyValuePair<CacheKey, IDictionary<string, string?>>(k, new Dictionary<string, string?> { ["f"] = $"{value}-{k.Name}" })).ToArray();

    private sealed class Harness : IDisposable
    {
        private readonly object _gate = new();
        private readonly bool _cacheNullValues;

        public Harness(int chunk = 500, int concurrent = 1, int? threshold = null, bool cacheNullValues = false)
        {
            Clock = TimeProvider.System;
            _cacheNullValues = cacheNullValues;
            var options = new InMemoryRedisCacheOptions { BatchChunkSize = chunk, BatchConcurrentChunks = concurrent, BatchTopicInvalidationThreshold = threshold, CacheNullValues = cacheNullValues };
            var inner = Substitute.For<IHashCache, ISpanKeyHashCache>();
            Telemetry = Substitute.For<ICachingTelemetryProvider>();
            OnRemove = _ => new ValueTask<bool>(true);
            OnRemoveFields = _ => new ValueTask<bool>(true);
            OnWrite = _ => Task.FromResult(true);
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"write:{key}");
                    Store(key, call.ArgAt<IDictionary<string, string?>>(1), call.ArgAt<HashCacheEntryOptions>(2).SetOption);
                    return new ValueTask<bool>(OnWrite!(key));
                });
            ValueTaskStubs.Returns(
                inner.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"remove:{key}");
                    lock (_gate)
                    {
                        L2.Remove(key);
                    }

                    return OnRemove!(key);
                });
            ValueTaskStubs.Returns(
                ((ISpanKeyHashCache)inner).RemoveFieldsAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"fields:{call.ArgAt<CacheKey>(0).Name}:{string.Join(',', call.ArgAt<string[]>(1))}");
                    return OnRemoveFields!(call.ArgAt<CacheKey>(0).Name);
                });
            ValueTaskStubs.Returns(
                inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Value = null, Expiration = DateTimeOffset.MinValue }));
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(new RecordingTopic(this));
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerHashCache(
                KnownCacheProviderNames.InMemoryRedis,
                inner,
                new MemoryCacheFactory(Clock, NullLoggerFactory.Instance),
                NullChangeTokenFactory.Instance,
                topics,
                events,
                Telemetry,
                options,
                options,
                new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
                NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                Clock,
                NullLogger.Instance));
        }

        public TimeProvider Clock { get; }

        public MultilayerHashCache Cache { get; }

        public ICachingTelemetryProvider Telemetry { get; }

        public List<string> Log { get; } = [];

        /// <summary>What the inner tier holds after the writes, as Redis would.</summary>
        public Dictionary<string, Dictionary<string, string?>> L2 { get; } = [];

        public Func<string, ValueTask<bool>>? OnRemove { get; set; }

        public Func<string, ValueTask<bool>>? OnRemoveFields { get; set; }

        public Func<string, Task<bool>>? OnWrite { get; set; }

        public Func<ICacheEvent, ValueTask<bool>>? OnPublish { get; set; }

        public string? Local(string key) =>
            Cache.GetAsync<string>(key, Awaited, Ct).AsTask().GetAwaiter().GetResult() is { } values && values.TryGetValue("f", out var f) ? f : null;

        public int Count(string prefix)
        {
            lock (_gate)
            {
                return Log.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public ValueTask<bool> Publish(ICacheEvent @event)
        {
            Add($"event:{@event.Type}:{@event.Data?.Key}");
            return OnPublish?.Invoke(@event) ?? new ValueTask<bool>(true);
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

        public void Dispose() => Cache.Dispose();

        private void Store(string key, IDictionary<string, string?> values, HashCacheSetOption setOption)
        {
            lock (_gate)
            {
                if (setOption == HashCacheSetOption.HashReplace && L2.TryGetValue(key, out var current))
                {
                    foreach (var field in values)
                    {
                        current[field.Key] = field.Value;
                    }
                }
                else if (values.Count == 0 && !_cacheNullValues)
                {
                    L2.Remove(key);
                }
                else if (values.Count == 0)
                {
                    // Redis writes a marker field with KeyReplace: the hash is there and empty.
                    L2[key] = [];
                }
                else
                {
                    L2[key] = new Dictionary<string, string?>(values);
                }
            }
        }

        private void Add(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
            }
        }
    }

    private sealed class RecordingTopic(Harness harness) : ITopic<ICacheEvent>
    {
        public TopicKey TopicKey { get; } = new("topic");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => new Nothing();

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => harness.Publish(@event);

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
}
