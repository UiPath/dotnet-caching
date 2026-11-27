using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RouterRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly string _cacheName = $"Rt{Guid.NewGuid():N}"[..12];
    private readonly string _app = $"rt{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private DateTimeOffset _serverTime;
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
            _serverTime = await _redis.GetServer(_redis.GetEndPoints()[0]).TimeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        if (_redis is not null)
        {
            foreach (var key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"n:{_cacheName},c:*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task Library_batch_writes_and_generates_the_mapped_redis_keys_with_the_requested_expiry()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 and REDIS_CONNECTION_STRING to run.");
        var provider = NewNode("batch", () => true, TimeSpan.Zero);
        var library = provider.GetRequiredKeyedService<ICacheFactory>(RouterServiceCollectionExtensions.StackName).CreateCache(KnownCacheProviderNames.Redis);
        var batch = new LibraryBatch<List<long>>(library, RedisKeyOf, null, null);
        var deadline = provider.GetRequiredService<TimeProvider>().ToDateTimeOffset(Lifetime);

        (await batch.SetAsync([new("one", [1]), new("two", [2])], deadline, Ct)).Should().BeTrue();
        ((byte[]?)await Db.StringGetAsync(RedisKeyOf("one"))).Should().Equal(LegacyEnvelope.Write(new List<long> { 1 }));
        ((byte[]?)await Db.StringGetAsync(RedisKeyOf("two"))).Should().Equal(LegacyEnvelope.Write(new List<long> { 2 }));
        (await Db.KeyTimeToLiveAsync(RedisKeyOf("two"))).Should().BeCloseTo(Lifetime, TimeSpan.FromSeconds(15));
        (await batch.GetAsync(["one", "missing", "two"], Ct)).Select(pair => pair.Value).Should().BeEquivalentTo(new List<long>?[] { [1], null, [2] }, options => options.WithStrictOrdering());

        KeyValuePair<string, int>[] entries = [new("one", 7), new("three", 3)];
        var values = await batch.GetOrAddAsync(entries, Generate, CacheExpiry.After(Lifetime), Ct);
        values.Should().BeEquivalentTo(new KeyValuePair<int, List<long>?>[] { new(7, [1]), new(3, [3]) });
        ((byte[]?)await Db.StringGetAsync(RedisKeyOf("three"))).Should().Equal(LegacyEnvelope.Write(new List<long> { 3 }));
        (await Db.KeyTimeToLiveAsync(RedisKeyOf("three"))).Should().BeCloseTo(Lifetime, TimeSpan.FromSeconds(15));
        (await batch.RemoveAsync(["one", "two", "three"], Ct)).Should().BeTrue();
        (await Db.KeyExistsAsync([RedisKeyOf("one"), RedisKeyOf("two"), RedisKeyOf("three")])).Should().Be(0);

        static Task<KeyValuePair<int, List<long>?>[]> Generate(int[] missing, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            missing.Should().Equal(3);
            return Task.FromResult(new KeyValuePair<int, List<long>?>[] { new(3, [3]) });
        }
    }

    [Fact]
    public async Task A_source_write_is_read_by_the_library_path_and_a_library_write_is_read_back_with_identical_bytes_and_key_text()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var library = false;
        var cache = Cache(NewNode("a", () => library, TimeSpan.Zero));
        var value = new List<long> { 1, 2, 3 };

        await cache.SetAsync(CacheKey.Verbatim("Mixed:Key 1"), value, Lifetime, Ct);

        var viaSource = (byte[]?)await Db.StringGetAsync(RedisKeyOf("Mixed:Key 1"));
        viaSource.Should().Equal(LegacyEnvelope.Write(value));
        library = true;
        (await cache.GetAsync(CacheKey.Verbatim("Mixed:Key 1"), Ct)).Should().Equal(value);

        await cache.SetAsync(CacheKey.Verbatim("Mixed:Key 2"), value, Lifetime, Ct);

        var viaLibrary = (byte[]?)await Db.StringGetAsync(RedisKeyOf("Mixed:Key 2"));
        viaLibrary.Should().Equal(viaSource, "both paths write the same envelope bytes");
        (await Db.KeyTimeToLiveAsync(RedisKeyOf("Mixed:Key 2")))!.Value.Should().BeCloseTo(Lifetime, TimeSpan.FromSeconds(10));
        library = false;
        (await cache.GetAsync(CacheKey.Verbatim("Mixed:Key 2"), Ct)).Should().Equal(value, "the source reads what the library wrote");
        _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"n:{_cacheName},c:*").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_router_with_value_serializers_and_no_write_format_setting_starts_its_library_whatever_its_format_negotiation_section_holds()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var malformed = new Dictionary<string, string?> { ["Caching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:00", ["Caching:Redis:FormatNegotiation:Mode"] = "Force" };

        var router = NewNode("a", () => true, TimeSpan.Zero, extra: malformed, serializers: true).GetRequiredService<IRouter>();

        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        router.Mode.Should().Be(RouterMode.Library, "a malformed section is skipped, as before");
    }

    [Fact]
    public async Task A_flip_runs_no_extra_factory_and_leaves_the_entry_where_it_was()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var library = false;
        var cache = Cache(NewNode("a", () => library, TimeSpan.Zero));
        var runs = 0;
        Task<List<long>?> Factory(CancellationToken token)
        {
            runs++;
            return Task.FromResult<List<long>?>([7]);
        }

        await cache.GetOrAddAsync("k", Factory, Lifetime, Ct);
        library = true;
        var afterFlip = await cache.GetOrAddAsync("k", Factory, Lifetime, Ct);
        library = false;
        var back = await cache.GetOrAddAsync("k", Factory, Lifetime, Ct);

        afterFlip.Should().Equal(7);
        back.Should().Equal(7);
        runs.Should().Be(1);
    }

    [Theory]
    [InlineData(KnownTopicNames.RedisStreams, true)]
    [InlineData(KnownTopicNames.RedisStreams, false)]
    [InlineData(KnownTopicNames.RedisPubSub, true)]
    public async Task A_peer_write_evicts_the_local_copy_on_another_node(string transport, bool peerOnLibrary)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var readerNode = NewNode("reader", () => true, TimeSpan.FromMinutes(5), transport);
        var writerNode = NewNode("writer", () => peerOnLibrary, TimeSpan.FromMinutes(5), transport);
        var reader = Cache(readerNode);
        var writer = Cache(writerNode);
        await Task.WhenAll(ReadyAsync(readerNode), ReadyAsync(writerNode));
        // Seeded by the reader, whose own event it ignores: a peer's seed event could land after the fill and evict it.
        await reader.SetAsync("k", [1], Lifetime, Ct);
        (await reader.GetAsync("k", Ct)).Should().Equal(1);
        await Db.KeyDeleteAsync(RedisKeyOf("k"));
        // Redis no longer has the entry, so only the local copy can answer.
        (await reader.GetAsync("k", Ct)).Should().Equal(1);

        var writerSource = ((Router)writerNode.GetRequiredService<IRouter>()).Library!.Source;
        await RedisTopicSignals.ObserveChangeAsync(
            Topic(writerNode),
            Topic(readerNode),
            () => writer.SetAsync("k", [2], Lifetime, Ct).AsTask(),
            Ct,
            message => message.Key == "k" && message.Source == writerSource);
        (await reader.GetAsync("k", Ct)).Should().Equal([2L], "the event evicted the stale copy");
    }

    [Theory]
    [InlineData(KnownTopicNames.RedisStreams)]
    [InlineData(KnownTopicNames.RedisPubSub)]
    public async Task A_clear_on_one_node_drops_the_local_copies_of_another(string transport)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var firstNode = NewNode("first", () => true, TimeSpan.FromMinutes(5), transport);
        var secondNode = NewNode("second", () => true, TimeSpan.FromMinutes(5), transport);
        var first = Cache(firstNode);
        var second = Cache(secondNode);
        await Task.WhenAll(ReadyAsync(firstNode), ReadyAsync(secondNode));
        await first.SetAsync("a", [1], Lifetime, Ct);
        await first.SetAsync("b", [2], Lifetime, Ct);
        (await second.GetAsync("a", Ct)).Should().Equal(1);
        (await second.GetAsync("b", Ct)).Should().Equal(2);

        await RedisTopicSignals.ObserveChangeAsync(
            Topic(firstNode),
            Topic(secondNode),
            () => ((RoutedCache<List<long>>)first).ClearAsync(Ct).AsTask(),
            Ct);
        (await second.GetAsync("a", Ct)).Should().BeNull();
        (await second.GetAsync("b", Ct)).Should().BeNull();
        (await Db.KeyExistsAsync(RedisKeyOf("a"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_value_the_envelope_cannot_read_is_a_counted_miss_that_leaves_the_key_in_place()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        using var meters = new MeterCollector("lists");
        var cache = Cache(NewNode("a", () => true, TimeSpan.Zero));
        await Db.StringSetAsync(RedisKeyOf("bad"), "not an envelope", Lifetime);

        var value = await cache.GetAsync("bad", Ct);

        value.Should().BeNull();
        meters.Count("unreadable").Should().BeGreaterThanOrEqualTo(1);
        meters.Count("unreadable", "path_reason", "other_client_unreadable").Should().Be(1);
        (await Db.KeyExistsAsync(RedisKeyOf("bad"))).Should().BeTrue("the library does not delete what it cannot read");
    }

    private static ICache<List<long>> Cache(ServiceProvider node) => node.GetRequiredService<IRouter>().Get<List<long>>("lists");

    private static ITopic<ICacheEvent> Topic(ServiceProvider node) =>
        ((Router)node.GetRequiredService<IRouter>()).Library!.Topics.Create(((IRouterParticipant)Cache(node)).Topic!.Value);

    private static Task ReadyAsync(ServiceProvider node) => RedisTopicSignals.ReadyAsync(node, Ct);

    private string RedisKeyOf(string key) => $"n:{_cacheName},c:{key}";

    private ServiceProvider NewNode(string name, Func<bool> useLibrary, TimeSpan localCap, string transport = KnownTopicNames.RedisStreams, Dictionary<string, string?>? extra = null, bool serializers = false)
    {
        var pubSub = transport == KnownTopicNames.RedisPubSub;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = _app,
            ["Caching:SourceUri"] = $"urn:router-{name}-{_app}",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = $"{fixture.ConnectionString},name=rt{name}",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:DefaultTopic"] = transport,
            ["Caching:Broadcast:RedisPubSub:Enabled"] = pubSub ? "true" : "false",
            ["Caching:Broadcast:RedisStreams:Enabled"] = pubSub ? "false" : "true",
        }).AddInMemoryCollection(RedisTopicSignals.SubscribeAtOnce).AddInMemoryCollection(extra ?? []).Build();
        var source = new LegacySource(_redis!, _cacheName);
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new SignalingTimeProvider(_serverTime));
        RedisTopicSignals.Add(services, "router:lists", streams: !pubSub);
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .Add(new RouterCacheRegistration<List<long>>
                    {
                        Name = "lists",
                        Group = "g",
                        Source = source,
                        RedisKey = source.RedisKey,
                        LocalCap = localCap,
                        DefaultExpiry = Lifetime,
                    })
                    .UseLibraryWhen(_ => useLibrary())
                    .UseEnvelope(new LegacyEnvelopeProxy());
                if (serializers)
                {
                    // A registered format is what makes the stack's negotiation validate its section.
                    router.UseValueSerializers(stack => stack.AddValueSerializer("messagepack", [TestFormatSerializer.Id], _ => new TestFormatSerializer()));
                }
            });
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }
}
