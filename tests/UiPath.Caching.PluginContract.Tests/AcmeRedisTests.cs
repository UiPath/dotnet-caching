using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Broadcast;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.PluginContract.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PluginContract.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AcmeRedisTests(RedisFixture fixture) : AcmeNodeTests(fixture)
{
    [Fact]
    public async Task A_policy_that_selects_the_plugin_round_trips_strings_and_hashes_under_its_ids()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a"));
        var cache = Cache(node);
        var hashes = node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var small = Unique();
        var large = Unique();
        var bigValue = new Profile(new string('x', AcmeValueSerializer.GzipThreshold * 16), 2);

        await cache.SetAsync(small, new Profile("small", 1), PolicyOf(node, Policy), Ct);
        await cache.SetAsync(large, bigValue, PolicyOf(node, Policy), Ct);
        var key = Unique();
        await hashes.SetAsync(key, new Dictionary<string, Profile?> { ["a"] = new("one", 1) }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), PolicyOf(node, Policy), Ct);

        (await Raw(small))!.Take(2).Should().Equal(0x1F, 0x00);
        (await Raw(large))!.Take(2).Should().Equal(new byte[] { 0x1F, 0x01 }, "a large payload takes the plugin's own gzip id");
        (await cache.GetAsync<Profile>(small, PolicyOf(node, Policy), Ct)).Should().Be(new Profile("small", 1));
        (await cache.GetAsync<Profile>(large, policy: null, Ct)).Should().Be(bigValue, "a reader without a policy finds the id in the header");
        (await hashes.GetAsync<Profile>(key, PolicyOf(node, Policy), Ct)).Should().ContainKey("a").WhoseValue.Should().Be(new Profile("one", 1));
        (await RawField(key, "a"))!.Take(2).Should().Equal(0x1F, 0x00);
    }

    [Fact]
    public async Task A_corrupt_or_truncated_body_is_a_miss_that_keeps_the_key()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a"));
        var cache = Cache(node);
        var key = Unique();
        await cache.SetAsync(key, new Profile("good", 1), PolicyOf(node, Policy), Ct);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(Fixture.ConnectionString);
        var redisKey = FindKey(redis, key);
        var ttl = TimeSpan.FromMinutes(1);

        await redis.GetDatabase().StringSetAsync(redisKey, new byte[] { 0x1F, 0x00, (byte)'{', (byte)'"' }, ttl);
        (await cache.GetAsync<Profile>(key, PolicyOf(node, Policy), Ct)).Should().BeNull();
        await redis.GetDatabase().StringSetAsync(redisKey, new byte[] { 0x1F, 0x01, 1, 2, 3 }, ttl);
        (await cache.GetAsync<Profile>(key, PolicyOf(node, Policy), Ct)).Should().BeNull();

        (await redis.GetDatabase().KeyExistsAsync(redisKey)).Should().BeTrue();
    }

    [Fact]
    public async Task A_read_list_that_leaves_the_plugin_out_reads_its_values_as_misses_and_one_that_names_it_reads_them()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a"));
        var cache = Cache(node);
        var key = Unique();
        await cache.SetAsync(key, new Profile("x", 1), PolicyOf(node, Policy), Ct);

        (await cache.GetAsync<Profile>(key, PolicyOf(node, Narrow), Ct)).Should().BeNull("the Narrow policy reads json only");
        (await cache.GetAsync<Profile>(key, PolicyOf(node, Migrating), Ct)).Should().Be(new Profile("x", 1), "the Migrating policy names acme in Read");
        (await Raw(key))![0].Should().Be(0x1F, "a miss keeps the stored value");
    }

    [Fact]
    public async Task Migrate_on_read_rewrites_json_into_the_plugin_and_never_back_to_json()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetry();
        var node = Node(new("a") { Telemetry = telemetry });
        var cache = Cache(node);
        var json = Unique();
        var stored = Unique();
        await cache.SetAsync(json, new Profile("x", 1), policy: null, Ct);
        await cache.SetAsync(stored, new Profile("p", 2), PolicyOf(node, Policy), Ct);
        (await Raw(json))![0].Should().Be((byte)'{');

        await cache.GetAsync<Profile>(stored, PolicyOf(node, Plain), Ct);
        (await cache.GetAsync<Profile>(json, PolicyOf(node, Migrating), Ct)).Should().Be(new Profile("x", 1));

        await telemetry.WaitForMetricAsync(Metrics.SerializerRewrite, "outcome", "done", Ct);
        (await Raw(json))!.Take(2).Should().Equal(0x1F, 0x00);
        (await Raw(stored))![0].Should().Be(0x1F, "a policy that writes json does not move a plugin value back");
    }

    [Theory]
    [InlineData(true, 0x10)]
    [InlineData(false, 0x00)]
    public async Task Migrate_on_read_moves_a_plugin_value_to_another_plugin_only_when_the_source_is_deterministic(bool deterministic, int expectedLowByte)
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetry();
        var node = Node(new("a") { Deterministic = deterministic, WithBeta = true, Telemetry = telemetry });
        var cache = Cache(node);
        var key = Unique();
        await cache.SetAsync(key, new Profile("x", 1), PolicyOf(node, Policy), Ct);

        (await cache.GetAsync<Profile>(key, PolicyOf(node, ToBeta), Ct)).Should().Be(new Profile("x", 1));
        if (deterministic)
        {
            await telemetry.WaitForMetricAsync(Metrics.SerializerRewrite, "outcome", "done", Ct);
            (await Raw(key))![1].Should().Be((byte)expectedLowByte, "the value is moved to the other plugin");
            telemetry.Count(Metrics.SerializerRewrite, "outcome", "lossy").Should().Be(0);
            return;
        }

        await telemetry.WaitForMetricAsync(Metrics.SerializerRewrite, "outcome", "lossy", Ct);
        telemetry.Count(Metrics.SerializerRewrite, "outcome", "lossy").Should().BeGreaterThan(0);
        (await Raw(key))!.Take(2).Should().Equal(new byte[] { 0x1F, (byte)expectedLowByte }, "a lossy value is skipped, not moved");
    }

    [Fact]
    public async Task The_inventory_counts_the_plugin_by_name_and_as_unknown_once_it_is_not_registered()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a"));
        var cache = Cache(node);
        var run = $"inventory-{Guid.NewGuid():N}";
        await cache.SetAsync($"{run}-1", new Profile("a", 1), PolicyOf(node, Policy), Ct);
        await cache.SetAsync($"{run}-2", new Profile("b", 2), PolicyOf(node, Policy), Ct);
        await cache.SetAsync($"{run}-3", new Profile("c", 3), policy: null, Ct);

        var registered = await Inventory(node).CountFormatsAsync($"*{run}*", Ct);
        var without = await Inventory(Node(new("b") { Acme = false })).CountFormatsAsync($"*{run}*", Ct);

        registered.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 1, [AcmeFormats.Name] = 2 });
        without.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 1, [CacheValueFormats.UnknownName] = 2 });
    }

    [Fact]
    public async Task A_node_advertises_exactly_the_tokens_of_its_plugin_ids()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a") { Streams = true });
        await Start(node);

        var ad = (await Advertisements()).Single();
        Field(ad, "vr").Should().Be("json,100,101");
        Field(ad, "er").Should().Be("json,11");
    }

    [Fact]
    public async Task A_peer_applies_an_event_the_plugin_wrote_and_decoded()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var readerEvents = new AcmeEventFormat();
        var telemetry = new RecordingTelemetry();
        var writerNode = Node(new("a"));
        var readerNode = Node(new("b") { Events = readerEvents, Telemetry = telemetry, ObservedTopic = nameof(Profile) });
        var writer = Cache(writerNode, KnownCacheProviderNames.InMemoryRedis);
        var reader = Cache(readerNode, KnownCacheProviderNames.InMemoryRedis);
        var policy = PolicyOf(Nodes[0], Policy);
        var key = Unique();
        await writer.SetAsync(key, new Profile("first", 1), policy, Ct);

        // Drain the first write before priming so only the second write can refresh the reader's copy.
        _ = Topic<Profile>(readerNode);
        await ReadyAsync(readerNode);
        await BarrierAsync<Profile>(writerNode, readerNode);
        (await reader.GetAsync<Profile>(key, policy, Ct)).Should().Be(new Profile("first", 1));
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        readerEvents.OnDecode = message =>
        {
            if (message.Key?.EndsWith(key, StringComparison.Ordinal) == true)
            {
                decoded.TrySetResult();
            }
        };

        await writer.SetAsync(key, new Profile("second", 2), policy, Ct);
        await decoded.Task.WaitAsync(Ct);
        // A topic barrier ends dispatch, but the refresh installs the value asynchronously.
        await telemetry.WaitForMetricAsync("cache.peer_refresh.kept", "cache.name", KnownCacheProviderNames.InMemoryRedis, Ct);
        (await reader.GetAsync<Profile>(key, policy, Ct)).Should().Be(new Profile("second", 2), "the reader applied the peer's write");
        readerEvents.Decoded.Should().NotBeEmpty("the reader's plugin decoded a body that started with its id, so the writer did not fall back to JSON");
    }

    [Fact]
    public async Task A_peer_field_update_decoded_by_the_plugin_carries_the_types_the_core_reads()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var readerEvents = new AcmeEventFormat();
        var reads = new ProfilingSession();
        var writerNode = Node(new("a") { Events = new AcmeEventFormat(), PeerFieldUpdates = true, DefaultPolicy = true });
        var peerNode = Node(new("b") { Events = readerEvents, PeerFieldUpdates = true, DefaultPolicy = true, Profile = reads, ObservedTopic = nameof(Profile) });
        var writer = HashCache(writerNode);
        var peer = HashCache(peerNode);
        var clock = peerNode.GetRequiredService<TimeProvider>();
        var warm = Unique();
        (await writer.SetAsync(warm, Fields("a"), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        (await peer.GetAsync<Profile>(warm, Ct)).Should().HaveCount(1);
        await ReadyAsync(peerNode);
        (await writer.SetAsync(warm, Fields("w"), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        await BarrierAsync<Profile>(writerNode, peerNode);
        readerEvents.Decoded.Should().Contain(e => e.Data!.Properties!.ContainsKey(CacheEventPropertyNames.PeerFields), "the peer is reading before the measured update is published");
        var key = Unique();
        (await writer.SetAsync(key, Fields("a"), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        await BarrierAsync<Profile>(writerNode, peerNode);
        (await peer.GetAsync<Profile>(key, Ct)).Should().HaveCount(1);
        reads.FinishProfiling().Select(c => c.Command).Should().Contain(c => c.StartsWith('H') || c.StartsWith("EVAL", StringComparison.Ordinal), "the session sees the peer's own hash read");
        _ = reads.FinishProfiling().ToList();

        (await writer.SetAsync(key, Fields("b"), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(20), SetOption: HashCacheSetOption.HashReplace, Metadata: new Dictionary<string, string?> { ["m"] = "v" }), Ct)).Should().BeTrue();

        await BarrierAsync<Profile>(writerNode, peerNode);
        var expected = new Dictionary<string, Profile?> { ["a"] = new Profile("a", 1), ["b"] = new Profile("b", 1) };
        var entry = await peer.GetCacheEntryAsync<Profile>(key, Ct);
        var commands = reads.FinishProfiling().Select(c => c.Command).ToList();
        entry.Should().NotBeNull("the peer applied the field, metadata and expiry the event carried");

        entry!.Value.Should().BeEquivalentTo(expected, "the peer applied the field the event carried and kept the primed one");
        entry.Metadata.Should().Contain("m", "v");
        entry.Expiration.Should().BeAfter(clock.ToDateTimeOffset(TimeSpan.FromMinutes(15)), "the update's expiry replaced the primed one");
        commands.Should().NotContain(c => c.StartsWith('H'), "the peer read no hash from Redis");
        var withFields = readerEvents.Decoded.Where(e => e.Data?.Properties?.ContainsKey(CacheEventPropertyNames.PeerFields) == true).ToList();
        withFields.Should().NotBeEmpty("a peer-field update arrived as a plugin body");
        foreach (var properties in withFields.Select(e => e.Data!.Properties!))
        {
            properties[CacheEventPropertyNames.PeerFields].Should().Match(v => v is IReadOnlyDictionary<string, byte[]?> || v is IDictionary<string, string?>);
            foreach (var (name, value) in properties)
            {
                AssertType(name, value);
            }
        }

        (await RawField(key, "b"))!.Take(2).Should().Equal(0x1F, 0x00);

    }

    private static IHashCache HashCache(ServiceProvider node) =>
        node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private static void AssertType(string name, object? value)
    {
        switch (name)
        {
            case CacheEventPropertyNames.Version:
                value.Should().BeOfType<long>();
                break;
            case CacheEventPropertyNames.Expiration:
            case CacheEventPropertyNames.PeerExpiration:
                value.Should().BeOfType<DateTimeOffset>();
                break;
            case CacheEventPropertyNames.Metadata:
            case CacheEventPropertyNames.PeerMetadata:
                value.Should().BeAssignableTo<IDictionary<string, string?>>();
                break;
        }
    }

    private static RedisCacheProvider Inventory(ServiceProvider node) => node.GetServices<ICacheProvider>().OfType<RedisCacheProvider>().Single();

    private static Dictionary<string, Profile?> Fields(string field) =>
        new() { [field] = new Profile(field, 1) };

    public sealed record Profile(string Name, int Version);
}
