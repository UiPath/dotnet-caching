#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class MultilayerCacheManyTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task RemoveMany_covers_every_key_across_chunk_boundaries(int count)
    {
        using var h = new Harness(chunk: 4);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(count), Ct);

        failed.Should().BeEmpty();
        h.Log.Where(l => l.StartsWith("remove:", StringComparison.Ordinal)).Should().Equal(Keys(count).Select(k => $"remove:{k.Name}"));
        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal(Keys(count).Select(k => $"event:CacheRemoved:{k.Name}"));
    }

    [Fact]
    public async Task RemoveMany_removes_the_chunk_before_its_events_and_all_of_the_chunk_before_awaiting_any()
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
        h.Log.Take(4).Should().OnlyContain(l => l.StartsWith("remove:", StringComparison.Ordinal));
        h.Log.Skip(4).Should().OnlyContain(l => l.StartsWith("event:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveMany_drops_the_local_copy_of_every_key_first()
    {
        using var h = new Harness(chunk: 4);
        await h.Cache.SetAsync<string>("k1", "v", Awaited, Ct);
        h.Local("k1").Should().Be("v");
        h.OnRemove = _ => new ValueTask<bool>(false);

        await h.Cache.RemoveManyAsync<string>(Keys(3), Ct);

        h.Local("k1").Should().BeNull();
    }

    [Fact]
    public async Task RemoveMany_reports_the_failed_key_of_a_middle_chunk_and_carries_on()
    {
        using var h = new Harness(chunk: 4);
        h.OnRemove = key => key == "k5" ? new ValueTask<bool>(false) : key == "k6" ? throw new InvalidOperationException("down") : new ValueTask<bool>(true);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(12), Ct);

        failed.Select(k => k.Name).Should().Equal("k5", "k6");
        h.Count("remove:").Should().Be(12);
        h.Log.Should().Contain("event:CacheRemoved:k5", "a delete that failed may have landed, so it is announced as the single remove does");
        h.Log.Should().Contain("event:CacheRemoved:k11");
    }

    [Fact]
    public async Task RemoveMany_reports_a_key_whose_event_failed()
    {
        using var h = new Harness(chunk: 4);
        h.OnPublish = e => e.Key == "k2" ? throw new InvalidOperationException("stream") : e.Key == "k3" ? new ValueTask<bool>(false) : new ValueTask<bool>(true);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(5), Ct);

        failed.Select(k => k.Name).Should().Equal("k2", "k3");
    }

    [Fact]
    public async Task RemoveMany_of_nothing_does_nothing()
    {
        using var h = new Harness(chunk: 4);

        (await h.Cache.RemoveManyAsync<string>([], Ct)).Should().BeEmpty();

        h.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveMany_stops_issuing_when_cancelled_and_keeps_what_it_did()
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
    public async Task RemoveMany_reads_a_stream_in_chunks_without_materializing_it()
    {
        using var h = new Harness(chunk: 4, concurrent: 1);
        var produced = 0;
        IEnumerable<CacheKey> Stream()
        {
            for (var i = 0; i < 20; i++)
            {
                produced++;
                yield return $"k{i}";
            }
        }

        h.OnRemove = _ =>
        {
            (produced - h.Count("remove:")).Should().BeLessThanOrEqualTo(4 + 1, "the input is read one chunk ahead of the commands, not up front");
            return new ValueTask<bool>(true);
        };

        (await h.Cache.RemoveManyAsync<string>(Stream(), Ct)).Should().BeEmpty();
        h.Count("remove:").Should().Be(20);
    }

    [Fact]
    public async Task RemoveMany_keeps_at_most_the_configured_chunks_in_flight()
    {
        using var h = new Harness(chunk: 2, concurrent: 2);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnRemove = _ => new ValueTask<bool>(gate.Task);

        var pending = h.Cache.RemoveManyAsync<string>(Keys(10), Ct).AsTask();

        await h.UntilAsync(() => h.Count("remove:") == 4);
        await Task.Delay(100, Ct);
        h.Count("remove:").Should().Be(4);
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
        h.Count("remove:").Should().Be(10);
    }

    [Fact]
    public async Task RemoveMany_waits_for_a_pending_background_write_of_a_key_and_only_of_those_keys()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnWrite = (key, _) => key == "k1" ? gate.Task : Task.FromResult(true);
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };
        await h.Cache.SetAsync<string>("k1", "v", background, Ct);

        var pending = h.Cache.RemoveManyAsync<string>(Keys(3), Ct).AsTask();

        await Task.Delay(100, Ct);
        pending.IsCompleted.Should().BeFalse();
        h.Count("remove:").Should().Be(0);
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
        h.Log.IndexOf("remove:k1").Should().BeGreaterThan(h.Log.IndexOf("write:k1=v"));
    }

    [Fact]
    public async Task RemoveMany_with_nothing_pending_does_not_wait_on_anything()
    {
        using var h = new Harness(chunk: 4);
        await h.Cache.SetAsync<string>("other", "v", new CachePolicy { BackgroundDistributedWrite = true }, Ct);
        await h.UntilAsync(() => h.Cache.RunsInFlight == 0);

        (await h.Cache.RemoveManyAsync<string>(Keys(3), Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SetMany_writes_each_chunk_before_its_events_and_keeps_the_values_locally(int count)
    {
        using var h = new Harness(chunk: 4, concurrent: 1);
        var pairs = Keys(count).Select(k => new KeyValuePair<CacheKey, string?>(k, $"v-{k.Name}")).ToArray();

        var failed = await h.Cache.SetManyAsync<string>(pairs, Awaited, Ct);

        failed.Should().BeEmpty();
        h.Count("write:").Should().Be(count);
        h.Count("event:").Should().Be(count);
        h.Log.Take(Math.Min(4, count)).Should().OnlyContain(l => l.StartsWith("write:", StringComparison.Ordinal));
        h.Local("k0").Should().Be("v-k0");
        h.Local($"k{count - 1}").Should().Be($"v-k{count - 1}");
    }

    [Fact]
    public async Task SetMany_gives_a_key_whose_write_failed_no_event_and_reports_it_while_the_rest_go_on()
    {
        using var h = new Harness(chunk: 4);
        h.OnWrite = (key, _) => key == "k5" ? Task.FromResult(false) : key == "k6" ? throw new InvalidOperationException("down") : Task.FromResult(true);
        var pairs = Keys(12).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")).ToArray();

        var failed = await h.Cache.SetManyAsync<string>(pairs, Awaited, Ct);

        failed.Select(k => k.Name).Should().Equal("k5", "k6");
        h.Log.Should().NotContain("event:CacheSet:k5").And.NotContain("event:CacheSet:k6");
        h.Log.Should().Contain("event:CacheSet:k4").And.Contain("event:CacheSet:k11");
    }

    [Fact]
    public async Task SetMany_reports_a_key_whose_event_failed_though_its_value_is_written_and_kept()
    {
        using var h = new Harness(chunk: 4);
        h.OnPublish = e => new ValueTask<bool>(e.Key != "k1");
        var pairs = Keys(3).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")).ToArray();

        var failed = await h.Cache.SetManyAsync<string>(pairs, Awaited, Ct);

        failed.Select(k => k.Name).Should().Equal("k1");
        h.Local("k1").Should().Be("v");
    }

    [Fact]
    public async Task SetMany_is_awaited_even_when_the_policy_asks_for_background_writes()
    {
        using var h = new Harness(chunk: 4);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnWrite = (_, _) => gate.Task;
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };

        var pending = h.Cache.SetManyAsync<string>([new("a", "1"), new("b", "2")], background, Ct).AsTask();

        await h.UntilAsync(() => h.Count("write:") == 2);
        pending.IsCompleted.Should().BeFalse();
        h.Cache.RunsInFlight.Should().Be(0);
        gate.SetResult(true);
        (await pending).Should().BeEmpty();
    }

    [Fact]
    public async Task SetMany_of_a_null_value_removes_the_key()
    {
        using var h = new Harness(chunk: 4);

        var failed = await h.Cache.SetManyAsync<string>([new("a", "1"), new("b", null)], Awaited, Ct);

        failed.Should().BeEmpty();
        h.Log.Should().Contain("write:a=1").And.Contain("remove:b").And.Contain("event:CacheRemoved:b");
    }

    [Fact]
    public async Task A_known_size_above_the_threshold_publishes_one_topic_wide_event_in_place_of_one_per_key()
    {
        using var h = new Harness(chunk: 4, threshold: 5);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(10), Ct);

        failed.Should().BeEmpty();
        h.Count("remove:").Should().Be(10);
        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal($"event:{KnownEventTypes.TopicInvalidated}:*");
        h.Log[^1].Should().Be($"event:{KnownEventTypes.TopicInvalidated}:*", "after every delete");
        h.Telemetry.Received(1).TrackEvent("cache.batch.topic_invalidation", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_set_above_the_threshold_announces_with_the_topic_wide_event_too()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        var pairs = Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")).ToArray();

        (await h.Cache.SetManyAsync<string>(pairs, Awaited, Ct)).Should().BeEmpty();

        h.Count("write:").Should().Be(10);
        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal($"event:{KnownEventTypes.TopicInvalidated}:*");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public async Task A_size_at_or_under_the_threshold_keeps_one_event_per_key(int count)
    {
        using var h = new Harness(chunk: 4, threshold: 5);

        await h.Cache.RemoveManyAsync<string>(Keys(count), Ct);

        h.Count("event:").Should().Be(count);
        h.Log.Should().NotContain(l => l.Contains(KnownEventTypes.TopicInvalidated, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stream_of_unknown_size_keeps_one_event_per_key_whatever_the_threshold()
    {
        using var h = new Harness(chunk: 4, threshold: 2);

        await h.Cache.RemoveManyAsync<string>(Keys(10).Where(_ => true), Ct);

        h.Count("event:").Should().Be(10);
    }

    [Fact]
    public async Task Without_a_threshold_a_big_batch_keeps_one_event_per_key()
    {
        using var h = new Harness(chunk: 4);

        await h.Cache.RemoveManyAsync<string>(Keys(50), Ct);

        h.Count("event:").Should().Be(50);
    }

    [Fact]
    public async Task A_topic_wide_event_that_fails_sends_every_key_back_to_the_caller()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        h.OnPublish = _ => new ValueTask<bool>(false);

        var failed = await h.Cache.RemoveManyAsync<string>(Keys(10), Ct);

        failed.Select(k => k.Name).Should().Equal(Keys(10).Select(k => k.Name));
    }

    [Fact]
    public async Task The_fallback_on_a_cache_without_the_capability_calls_the_single_key_members_and_reports_failures()
    {
        var cache = Substitute.For<ICache>();
        ValueTaskStubs.Returns(cache.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()), call => new ValueTask<bool>(call.ArgAt<CacheKey>(0).Name != "k1"));
        ValueTaskStubs.Returns(cache.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), call => new ValueTask<bool>(call.ArgAt<CacheKey>(0).Name != "k2"));

        var removed = await cache.RemoveManyAsync<string>(Keys(600), Ct);
        var set = await cache.SetManyAsync<string>(Keys(3).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")), (CachePolicy?)null, Ct);

        removed.Select(k => k.Name).Should().Equal("k1");
        set.Select(k => k.Name).Should().Equal("k2");
    }

    [Fact]
    public async Task Cache_of_T_gives_back_the_callers_own_keys_whatever_the_strategy_composed()
    {
        var inner = Substitute.For<ICache, ISpanKeyCache>();
        var multi = (ISpanKeyCache)inner;
        ValueTaskStubs.Returns(
            multi.RemoveManyAsync<string>(Arg.Any<IEnumerable<CacheKey>>(), Arg.Any<CancellationToken>()),
            call =>
            {
                var seen = call.ArgAt<IEnumerable<CacheKey>>(0).ToList();
                seen.Select(k => k.Name).Should().OnlyContain(n => n.StartsWith("p:", StringComparison.Ordinal));
                return new ValueTask<IReadOnlyList<CacheKey>>(seen.Where(k => k.Name.EndsWith('2')).ToList());
            });
        var cache = new Cache<string>(inner, new PrefixCacheKeyStrategy("p"));

        var counted = await cache.RemoveManyAsync(Keys(4), Ct);
        var stream = await cache.RemoveManyAsync(Keys(4).Where(_ => true), Ct);

        counted.Select(k => k.Name).Should().Equal("k2");
        stream.Select(k => k.Name).Should().Equal("k2");
    }

    [Fact]
    public async Task Cache_of_T_with_a_key_strategy_still_tells_the_batch_its_size()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        var cache = new Cache<string>(h.Cache, new PrefixCacheKeyStrategy("p"));
        var pairs = Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")).ToArray();

        (await cache.SetManyAsync(pairs, Ct)).Should().BeEmpty();

        h.Log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Should().Equal($"event:{KnownEventTypes.TopicInvalidated}:*");
    }

    [Fact]
    public async Task SetMany_of_a_key_named_twice_ends_as_its_last_write_leaves_it()
    {
        using var h = new Harness(chunk: 8);

        (await h.Cache.SetManyAsync<string>([new("a", "1"), new("a", null), new("b", null), new("b", "2")], Awaited, Ct)).Should().BeEmpty();

        h.Log.Should().Contain("remove:a").And.NotContain("write:a=1");
        h.Log.Should().Contain("write:b=2").And.NotContain("remove:b");
        h.Local("b").Should().Be("2");
    }

    [Fact]
    public async Task RemoveMany_cancelled_while_its_deletes_are_on_the_wire_throws_rather_than_reporting_them_failed()
    {
        using var h = new Harness(chunk: 4);
        using var cancel = new CancellationTokenSource();
        h.OnRemove = key =>
        {
            cancel.Cancel();
            return new ValueTask<bool>(Task.FromCanceled<bool>(cancel.Token));
        };

        var act = async () => await h.Cache.RemoveManyAsync<string>(Keys(4), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Log.Should().Contain("event:CacheRemoved:k0", "the delete issued before the cancel may have landed");
    }

    [Fact]
    public async Task A_topic_wide_set_cancelled_on_the_wire_drops_the_old_local_copy_and_still_announces()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        (await h.Cache.SetAsync<string>("k0", "old", Awaited, Ct)).Should().BeTrue();
        using var cancel = new CancellationTokenSource();
        h.OnWrite = (_, _) =>
        {
            cancel.Cancel();
            return Task.FromCanceled<bool>(cancel.Token);
        };

        var act = async () => await h.Cache.SetManyAsync<string>(Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")), Awaited, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.OnWrite = (_, _) => Task.FromResult(true);
        h.Local("k0").Should().BeNull("the cut-short write may have landed, so the old value must not stay");
        h.Log.Should().Contain($"event:{KnownEventTypes.TopicInvalidated}:*");
    }

    [Fact]
    public async Task A_topic_wide_batch_cancelled_before_its_first_chunk_flushes_nothing()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var act = async () => await h.Cache.SetManyAsync<string>(Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")), Awaited, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Log.Should().BeEmpty("nothing was written, so no node has anything to drop");
    }

    [Fact]
    public async Task A_topic_wide_batch_cancelled_after_its_first_chunk_flushes_the_topic_once()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        using var cancel = new CancellationTokenSource();
        h.OnWrite = (key, _) =>
        {
            if (key == "k3")
            {
                cancel.Cancel();
            }

            return Task.FromResult(true);
        };

        var act = async () => await h.Cache.SetManyAsync<string>(Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")), Awaited, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Count("write:").Should().Be(4);
        h.Log.Count(l => l == $"event:{KnownEventTypes.TopicInvalidated}:*").Should().Be(1);
    }

    [Fact]
    public async Task A_topic_wide_set_whose_writes_all_landed_announces_though_the_caller_cancels_after()
    {
        using var h = new Harness(chunk: 4, threshold: 5);
        using var cancel = new CancellationTokenSource();
        h.OnWrite = (key, _) =>
        {
            if (key == "k9")
            {
                cancel.Cancel();
            }

            return Task.FromResult(true);
        };

        try
        {
            await h.Cache.SetManyAsync<string>(Keys(10).Select(k => new KeyValuePair<CacheKey, string?>(k, "v")), Awaited, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Either outcome is the caller's; the event is not.
        }

        h.Log.Should().Contain($"event:{KnownEventTypes.TopicInvalidated}:*");
    }

    [Fact]
    public async Task A_topic_wide_event_expires_the_observers_of_the_topic_as_a_loss()
    {
        var subject = new KeyedSubject<ICacheEvent>(NullLogger.Instance);
        var observer = new MissObserver();
        subject.Subscribe(observer);

        subject.OnNext(new TestCacheEvent { Id = "1", Type = KnownEventTypes.TopicInvalidated, Source = new Uri("urn:other"), Data = new CacheEventData(KnownFieldNames.AllKeys) });

        observer.Missed.Should().Equal(MissedEventsReason.Lost);
        observer.Events.Should().Be(0);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_topic_wide_event_is_a_valid_event_an_older_node_reads_and_dispatches_by_its_key_alone()
    {
        var formatter = new CacheEventFormatter();
        var ev = new CacheEvent { Id = "1", Type = KnownEventTypes.TopicInvalidated, Source = new Uri("urn:other"), Data = new CacheEventData(KnownFieldNames.AllKeys) };

        var decoded = formatter.Decode(formatter.Encode(ev))!;

        decoded.IsValid().Should().BeTrue();
        decoded.Key.Should().Be("*");
        decoded.Type.Should().Be("TopicInvalidated");
    }

    private static CacheKey[] Keys(int count) => Enumerable.Range(0, count).Select(i => (CacheKey)$"k{i}").ToArray();

    private sealed class MissObserver : IKeyedObserver<ICacheEvent>, IMissedEventsObserver
    {
        public List<MissedEventsReason> Missed { get; } = [];

        public int Events { get; private set; }

        public string Key => "some-key";

        public void OnNext(ICacheEvent value) => Events++;

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void OnEventsMissed(MissedEventsReason reason) => Missed.Add(reason);
    }

    private sealed class Harness : IDisposable
    {
        private readonly object _gate = new();

        public Harness(int chunk = 500, int concurrent = 1, int? threshold = null)
        {
            Clock = TimeProvider.System;
            var options = new InMemoryRedisCacheOptions { BatchChunkSize = chunk, BatchConcurrentChunks = concurrent, BatchTopicInvalidationThreshold = threshold };
            var inner = Substitute.For<ICache>();
            Telemetry = Substitute.For<ICachingTelemetryProvider>();
            OnRemove = _ => new ValueTask<bool>(true);
            OnWrite = (_, _) => Task.FromResult(true);
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"write:{key}={call.ArgAt<string?>(1)}");
                    return new ValueTask<bool>(OnWrite!(key, call.ArgAt<CancellationToken>(4)));
                });
            ValueTaskStubs.Returns(
                inner.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"remove:{key}");
                    return OnRemove!(key);
                });
            ValueTaskStubs.Returns(
                inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue }));
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(new RecordingTopic(this));
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerCache(
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

        public MultilayerCache Cache { get; }

        public ICachingTelemetryProvider Telemetry { get; }

        public List<string> Log { get; } = [];

        public Func<string, ValueTask<bool>>? OnRemove { get; set; }

        public Func<string, CancellationToken, Task<bool>>? OnWrite { get; set; }

        public Func<ICacheEvent, ValueTask<bool>>? OnPublish { get; set; }

        public string? Local(string key) => Cache.GetAsync<string>(key, Awaited, Ct).AsTask().GetAwaiter().GetResult();

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
