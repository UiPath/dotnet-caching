#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>What the local tier keeps after a <see cref="HashCacheSetOption.HashReplace"/> write, which merges fields into the stored hash instead of replacing it.</summary>
public class MultilayerHashCacheReplaceTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static readonly HashCacheEntryOptions Merge = new(SetOption: HashCacheSetOption.HashReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_merge_into_a_hash_held_locally_keeps_its_other_fields()
    {
        using var h = new Rig();
        await h.Cache.SetAsync<string>("k", Fields(("a", "1"), ("b", "2")), Awaited, Ct);

        (await h.Cache.SetAsync<string>("k", Fields(("a", "9")), Merge, Awaited, Ct)).Should().BeTrue();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("b", "2")));
        h.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_merge_into_a_hash_not_held_locally_leaves_the_next_read_to_the_inner_tier()
    {
        using var h = new Rig();
        h.Stored = Fields(("a", "9"), ("b", "2"), ("c", "3"));

        (await h.Cache.SetAsync<string>("k", Fields(("a", "9")), Merge, Awaited, Ct)).Should().BeTrue();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("b", "2"), ("c", "3")));
        h.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_merge_to_an_absent_key_is_kept_when_the_local_tier_is_the_only_tier()
    {
        using var h = new Rig(memoryOnly: true);

        (await h.Cache.SetAsync<string>("k", Fields(("a", "1")), Merge, Awaited, Ct)).Should().BeTrue();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));
        (await h.Cache.GetItemAsync<string>("k", "a", Awaited, Ct)).Should().Be("1");
        (await h.Cache.SetAsync<string>("k", Fields(("b", "2")), Merge, Awaited, Ct)).Should().BeTrue();
        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
    }

    [Fact]
    public async Task A_background_merge_to_an_absent_key_is_kept_when_the_local_tier_is_the_only_tier()
    {
        using var h = new Rig(memoryOnly: true);

        await h.Cache.SetAsync<string>("k", Fields(("a", "1")), Merge, Background, Ct);
        await h.Settled();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));
    }

    [Fact]
    public async Task SetMany_merges_to_absent_keys_are_kept_when_the_local_tier_is_the_only_tier()
    {
        using var h = new Rig(memoryOnly: true);

        var failed = await h.Cache.SetManyAsync<string>([new("x", Fields(("a", "1"))), new("y", Fields(("b", "2")))], Merge, Awaited, Ct);

        failed.Should().BeEmpty();
        (await h.Cache.GetAsync<string>("x", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));
        (await h.Cache.GetAsync<string>("y", Awaited, Ct)).Should().BeEquivalentTo(Fields(("b", "2")));
    }

    [Fact]
    public async Task A_replace_of_the_whole_key_keeps_exactly_what_was_written()
    {
        using var h = new Rig();
        await h.Cache.SetAsync<string>("k", Fields(("a", "1"), ("b", "2")), Awaited, Ct);

        await h.Cache.SetAsync<string>("k", Fields(("a", "9")), new HashCacheEntryOptions(SetOption: HashCacheSetOption.KeyReplace), Awaited, Ct);

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9")));
        h.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_background_merge_keeps_the_other_fields_of_a_hash_held_locally()
    {
        using var h = new Rig();
        await h.Cache.SetAsync<string>("k", Fields(("a", "1"), ("b", "2")), Awaited, Ct);

        await h.Cache.SetAsync<string>("k", Fields(("a", "9")), Merge, Background, Ct);
        await h.Settled();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("b", "2")));
        h.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_background_merge_into_a_hash_not_held_locally_keeps_nothing_locally()
    {
        using var h = new Rig();
        h.Stored = Fields(("a", "9"), ("b", "2"));

        await h.Cache.SetAsync<string>("k", Fields(("a", "9")), Merge, Background, Ct);
        await h.Settled();

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("b", "2")));
        h.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task SetMany_merges_into_local_hashes_the_same_way()
    {
        using var h = new Rig();
        await h.Cache.SetAsync<string>("held", Fields(("a", "1"), ("b", "2")), Awaited, Ct);
        h.Stored = Fields(("a", "9"), ("z", "0"));

        var failed = await h.Cache.SetManyAsync<string>(
            [new("held", Fields(("a", "9"))), new("absent", Fields(("a", "9")))],
            Merge,
            Awaited,
            Ct);

        failed.Should().BeEmpty();
        (await h.Cache.GetAsync<string>("held", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("b", "2")));
        h.InnerReads.Should().Be(0);
        (await h.Cache.GetAsync<string>("absent", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "9"), ("z", "0")));
        h.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_merge_keeps_the_metadata_of_the_local_hash_when_the_write_carries_none()
    {
        using var h = new Rig();
        await h.Cache.SetAsync<string>("k", Fields(("a", "1")), new HashCacheEntryOptions(Metadata: new Dictionary<string, string?> { ["m"] = "x" }), Awaited, Ct);

        await h.Cache.SetAsync<string>("k", Fields(("b", "2")), Merge, Awaited, Ct);

        var entry = await h.Cache.GetCacheEntryAsync<string>("k", Awaited, Ct);
        entry.Metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "x" });
    }

    [Fact]
    public async Task Two_merges_of_one_hash_at_once_keep_both_sets_of_fields_locally()
    {
        using var h = new Rig();
        for (var round = 0; round < 300; round++)
        {
            var key = $"k{round}";
            await h.Cache.SetAsync<string>(key, Fields(("base", "0")), Awaited, Ct);
            using var start = new Barrier(2);
            var first = Task.Run(async () => { start.SignalAndWait(Ct); await h.Cache.SetAsync<string>(key, Fields(("a", "1")), Merge, Awaited, Ct); }, Ct);
            var second = Task.Run(async () => { start.SignalAndWait(Ct); await h.Cache.SetAsync<string>(key, Fields(("b", "2")), Merge, Awaited, Ct); }, Ct);
            await Task.WhenAll(first, second);

            (await h.Cache.GetAsync<string>(key, Awaited, Ct)).Keys.Should().BeEquivalentTo(["base", "a", "b"], "round {0}", round);
        }
    }

    [Fact]
    public async Task A_replace_racing_a_merge_never_brings_back_a_field_it_dropped()
    {
        using var h = new Rig();
        for (var round = 0; round < 300; round++)
        {
            var key = $"r{round}";
            await h.Cache.SetAsync<string>(key, Fields(("base", "0")), Awaited, Ct);
            using var start = new Barrier(2);
            var replace = Task.Run(async () => { start.SignalAndWait(Ct); await h.Cache.SetAsync<string>(key, Fields(("c", "3")), Awaited, Ct); }, Ct);
            var merge = Task.Run(async () => { start.SignalAndWait(Ct); await h.Cache.SetAsync<string>(key, Fields(("a", "1")), Merge, Awaited, Ct); }, Ct);
            await Task.WhenAll(replace, merge);

            (await h.Cache.GetAsync<string>(key, Awaited, Ct)).Keys.Should().NotContain("base", "round {0}", round);
        }
    }

    private static Dictionary<string, string?> Fields(params (string Name, string Value)[] fields) =>
        fields.ToDictionary(f => f.Name, f => (string?)f.Value);

    private sealed class Rig : IDisposable
    {
        private int _innerReads;

        public Rig(bool memoryOnly = false)
        {
            var options = new InMemoryRedisCacheOptions();
            var inner = Substitute.For<IHashCache>();
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                new ValueTask<bool>(true));
            ValueTaskStubs.Returns(
                inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                _ =>
                {
                    Interlocked.Increment(ref _innerReads);
                    return new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Value = Stored, Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });
                });
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            var topic = Substitute.For<ITopic<ICacheEvent>>();
            topic.TopicKey.Returns(new TopicKey("topic"));
            topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
            topic.Subscribe(Arg.Any<IObserver<ICacheEvent>>()).Returns(Substitute.For<IDisposable>());
            provider.Create(Arg.Any<TopicKey>()).Returns(topic);
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerHashCache(
                KnownCacheProviderNames.InMemoryRedis,
                memoryOnly ? NullHashCache.Instance : inner,
                new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
                NullChangeTokenFactory.Instance,
                topics,
                events,
                Substitute.For<ICachingTelemetryProvider>(),
                options,
                options,
                new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
                NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                TimeProvider.System,
                NullLogger.Instance));
        }

        public MultilayerHashCache Cache { get; }

        public IDictionary<string, string?>? Stored { get; set; }

        public int InnerReads => Volatile.Read(ref _innerReads);

        public async Task Settled()
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Cache.RunsInFlight != 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(5, Ct);
            }
        }

        public void Dispose() => Cache.Dispose();
    }
}
