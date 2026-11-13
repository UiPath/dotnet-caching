using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class SerializerIntegrationTests(RedisContainerFixture fixture)
{
    private const string FastPolicy = "fast";

    private readonly PassthroughValueSerializer _serializer = new(0x10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_policy_serializer_writes_a_headered_value_that_any_policy_reads()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();

        (await cache.SetAsync(key, "hello", policy, Ct)).Should().BeTrue();

        var stored = await RawValueAsync(raw, key);
        stored![0].Should().Be(0x10);
        System.Text.Encoding.UTF8.GetString(stored, 1, stored.Length - 1).Should().Be("hello");
        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().Be("hello");
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("hello");
        (await cache.GetCacheEntryAsync<string>(key, policy: null, Ct)).Value.Should().Be("hello");
    }

    [Fact]
    public async Task A_value_round_trips_through_the_registered_serializer_the_policy_picks()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        var key = Unique();

        await cache.SetAsync(key, "round trip", policy, Ct);
        var read = await cache.GetAsync<string>(key, policy, Ct);

        read.Should().Be("round trip");
        _serializer.Serialized.Should().Be(1);
        _serializer.Deserialized.Should().Be(1);
        _serializer.LastFormatId.Should().Be(0x10);
    }

    [Fact]
    public async Task Hash_fields_and_metadata_round_trip_through_the_registered_serializer_the_policy_picks()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var hashes = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var policy = provider.GetRequiredService<ICachePolicyFactory>().Resolve(FastPolicy)!;
        var key = Unique();
        var values = new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" };
        var options = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        (await hashes.SetAsync(key, values, options, policy, Ct)).Should().BeTrue();
        var entry = await hashes.GetCacheEntryAsync<string>(key, policy, Ct);

        entry.Value.Should().BeEquivalentTo(values);
        entry.Metadata.Should().BeEquivalentTo(options.Metadata);
        _serializer.Serialized.Should().Be(3);
        _serializer.Deserialized.Should().Be(3);
    }

    [Fact]
    public async Task A_value_written_without_a_policy_serializer_is_plain_json()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();

        await cache.SetAsync(key, "hello", policy: null, Ct);

        System.Text.Encoding.UTF8.GetString((await RawValueAsync(raw, key))!).Should().Be("\"hello\"");
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("hello");
    }

    [Fact]
    public async Task A_key_written_as_json_then_headered_then_json_reads_each_time()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        var key = Unique();

        await cache.SetAsync(key, "one", policy: null, Ct);
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("one");
        await cache.SetAsync(key, "two", policy, Ct);
        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().Be("two");
        await cache.SetAsync(key, "three", policy: null, Ct);
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("three");
    }

    [Fact]
    public async Task GetOrAdd_and_TryAdd_and_batch_writes_use_the_policy_serializer()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (added, generated, many) = (Unique(), Unique(), Unique());

        (await cache.TryAddAsync(added, "a", policy, Ct)).Should().BeTrue();
        (await cache.GetOrAddAsync(generated, _ => Task.FromResult<string?>("g"), policy, Ct)).Should().Be("g");
        await cache.SetAsync([new KeyValuePair<CacheKey, string?>(many, "m")], policy, Ct);

        foreach (var key in new[] { added, generated, many })
        {
            (await RawValueAsync(raw, key))![0].Should().Be(0x10, key);
        }

        (await cache.GetAsync<string>([added, generated, many], policy: null, Ct)).Select(v => v.Value).Should().Equal("a", "g", "m");
    }

    [Fact]
    public async Task A_value_in_an_unknown_format_is_a_miss_and_the_key_is_removed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, _) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        await cache.SetAsync(key, "seed", policy: null, Ct);
        var redisKey = RawKey(raw, key);
        await raw.GetDatabase().StringSetAsync(redisKey, new byte[] { 0x1E, 1, 2 });

        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().BeNull();

        (await raw.GetDatabase().KeyExistsAsync(redisKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_key_that_a_peer_rewrote_after_the_read_survives_the_removal_of_the_unreadable_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (cache, _) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        await cache.SetAsync(key, "seed", policy: null, Ct);
        var redisKey = RawKey(raw, key);
        var newer = new SystemJsonByteSerializerProxy().Serialize("newer")!;
        await raw.GetDatabase().StringSetAsync(redisKey, new byte[] { 0x1E, 1, 2 });
        telemetry.MetricRecorded = metric =>
        {
            if (metric.Name == "cache.serializer.unreadable")
            {
                raw.GetDatabase().StringSet(redisKey, newer);
            }
        };

        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().BeNull();

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(newer);
        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().Be("newer");
    }

    [Fact]
    public async Task A_key_that_a_peer_rewrote_after_a_batch_read_survives_the_removal_of_the_unreadable_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (cache, _) = CacheAndPolicy(provider);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (changed, stale) = (Unique(), Unique());
        await cache.SetAsync(changed, "seed", policy: null, Ct);
        await cache.SetAsync(stale, "seed", policy: null, Ct);
        var (changedKey, staleKey) = (RawKey(raw, changed), RawKey(raw, stale));
        var newer = new SystemJsonByteSerializerProxy().Serialize("newer")!;
        await raw.GetDatabase().StringSetAsync(changedKey, new byte[] { 0x1E, 1, 2 });
        await raw.GetDatabase().StringSetAsync(staleKey, new byte[] { 0x1E, 1, 2 });
        var rewritten = false;
        telemetry.MetricRecorded = metric =>
        {
            if (metric.Name == "cache.serializer.unreadable" && !rewritten)
            {
                rewritten = true;
                raw.GetDatabase().StringSet(changedKey, newer);
            }
        };

        await cache.GetAsync<string>([changed, stale], policy: null, Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(changedKey)).Should().Equal(newer);
        (await raw.GetDatabase().KeyExistsAsync(staleKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_node_with_no_registry_keeps_a_headered_value_and_one_with_another_registry_removes_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var writer = Build();
        using var noRegistry = Build(registry: "none");
        using var otherRegistry = Build(registry: "other");
        var (cache, policy) = CacheAndPolicy(writer);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        await cache.SetAsync(key, "headered", policy, Ct);
        var redisKey = RawKey(raw, key);
        var stored = (byte[]?)await raw.GetDatabase().StringGetAsync(redisKey);

        (await noRegistry.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis).GetAsync<string>(key, policy: null, Ct)).Should().BeNull();
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(stored);

        (await otherRegistry.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis).GetAsync<string>(key, policy: null, Ct)).Should().BeNull();
        (await raw.GetDatabase().KeyExistsAsync(redisKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_headered_value_whose_serializer_throws_is_a_miss_and_the_key_stays()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(registry: "throwing");
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        await cache.SetAsync(key, "seed", policy: null, Ct);
        var redisKey = RawKey(raw, key);
        byte[] stored = [0x10, 1, 2];
        await raw.GetDatabase().StringSetAsync(redisKey, stored);

        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().BeNull();

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(stored);
    }

    [Fact]
    public async Task A_hash_with_a_field_in_an_unknown_format_is_a_miss_and_the_hash_stays()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var hashes = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        await hashes.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy: null, Ct);
        var redisKey = RawKey(raw, key);
        await raw.GetDatabase().HashSetAsync(redisKey, "b", new byte[] { 0x1E, 1 });

        (await hashes.GetAsync<string>(key, policy: null, Ct)).Should().BeEmpty();

        (await raw.GetDatabase().HashGetAsync(redisKey, "b")).HasValue.Should().BeTrue();
        (await raw.GetDatabase().HashLengthAsync(redisKey)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1)]
    public async Task A_hash_replace_with_empty_metadata_drops_the_old_metadata_for_a_non_json_write(int fieldBatchSize)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(fieldBatchSize: fieldBatchSize);
        var hashes = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var policy = provider.GetRequiredService<ICachePolicyFactory>().Resolve(FastPolicy)!;
        var key = Unique();
        var values = new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" };
        var withMetadata = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });
        var replacing = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5), Metadata: new Dictionary<string, string?>(), SetOption: HashCacheSetOption.HashReplace);
        await hashes.SetAsync(key, values, withMetadata, policy, Ct);
        (await hashes.GetCacheEntryAsync<string>(key, policy, Ct)).Metadata.Should().NotBeNullOrEmpty();

        (await hashes.SetAsync(key, values, replacing, policy, Ct)).Should().BeTrue();

        var entry = await hashes.GetCacheEntryAsync<string>(key, policy, Ct);
        entry.Value.Should().BeEquivalentTo(values);
        entry.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task A_large_value_round_trips()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider);
        var key = Unique();
        var large = new string('x', 200 * 1024);

        await cache.SetAsync(key, large, policy, Ct);

        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().Be(large);
    }

    [Fact]
    public async Task Distributed_cache_bytes_that_start_with_a_header_byte_read_back_unchanged()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(distributed: true);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var key = Unique();

        await cache.SetAsync(key, [0x10, 1, 2], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, Ct);

        (await cache.GetAsync(key, Ct)).Should().Equal(0x10, 1, 2);
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static (ICache Cache, CachePolicy Policy) CacheAndPolicy(ServiceProvider provider) =>
        (provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis), provider.GetRequiredService<ICachePolicyFactory>().Resolve(FastPolicy)!);

    private static RedisKey RawKey(ConnectionMultiplexer raw, string key) =>
        raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    private static async Task<byte[]?> RawValueAsync(ConnectionMultiplexer raw, string key) =>
        await raw.GetDatabase().StringGetAsync(RawKey(raw, key));

    private ServiceProvider Build(bool distributed = false, string registry = "fast", RecordingTelemetryProvider? telemetry = null, int fieldBatchSize = 1000)
    {
        var services = new ServiceCollection();
        if (telemetry is not null)
        {
            services.AddSingleton<ICachingTelemetryProvider>(telemetry);
        }

        return services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.HashFieldBatchSize = fieldBatchSize;
                        o.FormatNegotiation.Mode = FormatNegotiationMode.Force;
                    });
                    switch (registry)
                    {
                        case "fast":
                            b.AddValueSerializer("fast", _ => _serializer);
                            break;
                        case "other":
                            b.AddValueSerializer("other", _ => new PassthroughValueSerializer(0x12));
                            break;
                        case "throwing":
                            b.AddValueSerializer("bad", _ => new ThrowingValueSerializer());
                            break;
                    }

                    if (distributed)
                    {
                        b.AddDistributedCache(KnownCacheProviderNames.Redis);
                    }
                },
                o =>
                {
                    o.AppShortName = "serializerit";
                    if (registry == "fast")
                    {
                        o.Policies[FastPolicy] = new CachePolicy
                        {
                            DistributedExpiration = TimeSpan.FromMinutes(5),
                            Serializer = new SerializerPolicy { Write = "fast" },
                        };
                    }
                })
            .BuildServiceProvider();
    }
}
