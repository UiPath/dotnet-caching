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
public class ReadListIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] JsonX = "\"x\""u8.ToArray();
    private static readonly byte[] FastX = [0x10, (byte)'x'];
    private static readonly byte[] JsonMeta = "{\"m\":\"v\"}"u8.ToArray();
    private static readonly byte[] FastMeta = [0x10, .. "{\"m\":\"v\"}"u8];

    private readonly PassthroughValueSerializer _serializer = new(0x10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // policy, whether the stored value is JSON (else headered), whether the policy reads it
    public static TheoryData<string, bool, bool> Cases() => new()
    {
        { "fastonly", true, false },
        { "fastonly", false, true },
        { "jsononly", true, true },
        { "jsononly", false, false },
        { "both", true, true },
        { "both", false, true },
        { "open", true, true },
        { "open", false, true },
    };

    public static TheoryData<string, string, bool, bool> StringOperations()
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var operation in new[] { "get", "entry", "batch", "getoradd", "getoradd-expiring" })
        {
            foreach (var (policy, storedJson, hit) in new[]
            {
                ("fastonly", true, false), ("fastonly", false, true), ("jsononly", true, true), ("jsononly", false, false), ("both", true, true), ("open", false, true),
            })
            {
                data.Add(operation, policy, storedJson, hit);
            }
        }

        return data;
    }

    public static TheoryData<string, string, bool, bool> HashOperations()
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var operation in new[] { "all", "item", "fields", "entry", "getoradd" })
        {
            foreach (var (policy, storedJson, hit) in new[]
            {
                ("fastonly", true, false), ("fastonly", false, true), ("jsononly", true, true), ("jsononly", false, false), ("both", true, true), ("open", false, true),
            })
            {
                data.Add(operation, policy, storedJson, hit);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StringOperations))]
    public async Task A_value_outside_the_read_list_is_a_miss_and_the_key_stays(string operation, string name, bool storedJson, bool hit)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider, name);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, storedJson ? JsonX : FastX);

        var read = await ReadAsync(cache, operation, key, policy);

        var regenerates = !hit && operation.StartsWith("getoradd", StringComparison.Ordinal);
        read.Should().Be(hit ? "x" : regenerates ? "generated" : null);
        if (regenerates)
        {
            // The generator's value is written in the policy's format over the one it could not read.
            (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("generated");
        }
        else
        {
            ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(storedJson ? JsonX : FastX);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_stored_format_is_read_or_a_miss_by_the_policys_list_and_is_never_removed(string name, bool storedJson, bool hit)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (cache, policy) = CacheAndPolicy(provider, name);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, storedJson ? JsonX : FastX);

        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be(hit ? "x" : null);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(storedJson ? JsonX : FastX);
    }

    [Fact]
    public async Task A_call_without_a_policy_reads_by_the_default_policys_list()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(defaultPolicy: Policy("json", ["json"]), named: false);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, FastX);

        (await cache.GetAsync<string>(key, policy: null, Ct)).Should().BeNull();

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
    }

    [Fact]
    public async Task The_write_format_is_readable_for_a_call_site_policy_that_leaves_it_out_of_its_list()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(defaultPolicy: Policy("fast", null));
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var readsJson = new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } };
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();

        await cache.SetAsync(key, "x", readsJson, Ct);

        (await RawValueAsync(raw, key))![0].Should().Be(0x10);
        (await cache.GetAsync<string>(key, readsJson, Ct)).Should().Be("x");
    }

    [Fact]
    public async Task A_call_site_policy_that_names_an_unregistered_read_format_still_writes_and_throws_on_read()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var unregistered = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5), Serializer = new SerializerPolicy { Read = ["json", "nope"] } };
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();

        await cache.SetAsync(key, "x", unregistered, Ct);

        (await RawValueAsync(raw, key)).Should().NotBeNull();
        var read = async () => await cache.GetAsync<string>(key, unregistered, Ct);
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nope*");
    }

    [Fact]
    public async Task A_miss_outside_the_list_is_counted_on_every_read_and_never_deletes()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (cache, policy) = CacheAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, JsonX);

        for (var i = 0; i < 3; i++)
        {
            (await cache.GetAsync<string>(key, policy, Ct)).Should().BeNull();
        }

        telemetry.Metrics.Count(m => m.Name == "cache.serializer.unreadable").Should().Be(3);
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(JsonX);
        (await cache.GetAsync<string>(key, provider.GetRequiredService<ICachePolicyFactory>().Resolve("both"), Ct)).Should().Be("x");
    }

    [Fact]
    public async Task A_batch_read_counts_each_miss_and_keeps_every_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (cache, policy) = CacheAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (outside, _) = await SeedAsync(cache, raw, JsonX);
        var (inside, _) = await SeedAsync(cache, raw, FastX);

        var read = await cache.GetAsync<string>([outside, inside], policy, Ct);

        read.Select(r => r.Value).Should().Equal(null, "x");
        telemetry.Metrics.Count(m => m.Name == "cache.serializer.unreadable").Should().Be(1);
        (await raw.GetDatabase().KeyExistsAsync(RawKey(raw, outside))).Should().BeTrue();
    }

    [Fact]
    public async Task In_auto_mode_a_call_site_list_without_json_still_reads_the_json_the_node_wrote_while_the_node_list_held_the_format_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(mode: FormatNegotiationMode.Auto, named: false);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var callSite = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5), Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();

        await cache.SetAsync(key, "x", callSite, Ct);

        System.Text.Encoding.UTF8.GetString((await RawValueAsync(raw, key))!).Should().Be("\"x\"", "no node list has allowed the format yet, so Auto wrote JSON");
        (await cache.GetAsync<string>(key, callSite, Ct)).Should().Be("x");
        (await RawValueAsync(raw, key)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("fastonly", true, "headerless")]
    [InlineData("jsononly", false, "0x10")]
    public async Task A_value_outside_the_list_is_counted_under_its_format(string name, bool storedJson, string format)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (cache, policy) = CacheAndPolicy(provider, name);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, _) = await SeedAsync(cache, raw, storedJson ? JsonX : FastX);

        await cache.GetAsync<string>(key, policy, Ct);

        telemetry.Metrics.Should().ContainSingle(m => m.Name == "cache.serializer.unreadable")
            .Which.Properties!["format"].Should().Be(format);
    }

    [Theory]
    [MemberData(nameof(HashOperations))]
    public async Task A_hash_field_outside_the_read_list_is_a_miss_and_the_hash_stays(string operation, string name, bool storedJson, bool hit)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, name);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, storedJson ? JsonX : FastX);

        var read = await ReadHashAsync(hashes, operation, key, policy);

        var regenerates = !hit && operation == "getoradd";
        read.Should().Be(hit ? "x" : regenerates ? "generated" : null);
        if (!regenerates)
        {
            ((byte[]?)await raw.GetDatabase().HashGetAsync(redisKey, "a")).Should().Equal(storedJson ? JsonX : FastX);
        }
    }

    [Theory]
    [InlineData("fastonly", false, false, true)]
    [InlineData("fastonly", false, true, false)]
    [InlineData("jsononly", true, true, true)]
    [InlineData("jsononly", true, false, false)]
    [InlineData("both", true, false, true)]
    [InlineData("open", false, true, true)]
    public async Task Metadata_outside_the_read_list_is_a_miss_like_a_field(string name, bool fieldJson, bool metadataJson, bool hit)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, name);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, fieldJson ? JsonX : FastX);
        await raw.GetDatabase().HashSetAsync(redisKey, "_metadata_", metadataJson ? JsonMeta : FastMeta);

        var entry = await hashes.GetCacheEntryAsync<string>(key, policy, Ct);

        entry.Found.Should().Be(hit);
        if (hit)
        {
            entry.Metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v" });
        }

        ((byte[]?)await raw.GetDatabase().HashGetAsync(redisKey, "_metadata_")).Should().Equal(metadataJson ? JsonMeta : FastMeta);
    }

    [Fact]
    public async Task Metadata_reads_in_whatever_format_a_named_policy_wrote_it_whatever_the_default_policy_lists()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(defaultPolicy: Policy("json", ["json"]), named: false, configure: o => o.Policies["writer"] = Policy("fast", ["json", "fast"]));
        var hashes = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var writer = provider.GetRequiredService<ICachePolicyFactory>().Resolve("writer")!;
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        var options = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        await hashes.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, options, writer, Ct);

        ((byte[]?)await raw.GetDatabase().HashGetAsync(RawKey(raw, key), "_metadata_"))![0].Should().Be(0x10);
        (await hashes.GetMetadataAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v" });
    }

    [Fact]
    public async Task A_hash_with_one_readable_and_one_unreadable_field_misses_whole_but_serves_the_readable_field()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, FastX);
        await raw.GetDatabase().HashSetAsync(redisKey, "b", JsonX);

        (await hashes.GetAsync<string>(key, policy, Ct)).Should().BeEmpty();
        (await hashes.GetAsync<string>(key, ["a"], policy, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
        (await hashes.GetItemAsync<string>(key, "b", policy, Ct)).Should().BeNull();

        ((byte[]?)await raw.GetDatabase().HashGetAsync(redisKey, "b")).Should().Equal(JsonX);
    }

    [Theory]
    [InlineData(HashCacheSetOption.HashReplace)]
    [InlineData(HashCacheSetOption.KeyReplace)]
    public async Task A_regenerated_hash_replaces_a_field_the_policy_cannot_read_so_the_next_read_hits(HashCacheSetOption option)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = Build(telemetry: telemetry);
        var (hashes, policy) = HashesAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, FastX);
        await raw.GetDatabase().HashSetAsync(redisKey, "b", JsonX);
        var generated = new Dictionary<string, string?> { ["a"] = "generated" };

        var first = await hashes.GetOrAddAsync<string>(key, _ => Task.FromResult<IDictionary<string, string?>>(generated), DateTimeOffset.UtcNow.AddMinutes(5), option, policy, Ct);
        var misses = telemetry.Metrics.Count(m => m.Name == "cache.serializer.unreadable");
        var second = await hashes.GetOrAddAsync<string>(key, _ => throw new InvalidOperationException("the regenerated hash did not hit"), DateTimeOffset.UtcNow.AddMinutes(5), option, policy, Ct);

        FieldA(first).Should().Be("generated");
        FieldA(second).Should().Be("generated");
        (await raw.GetDatabase().HashExistsAsync(redisKey, "b")).Should().BeFalse();
        telemetry.Metrics.Count(m => m.Name == "cache.serializer.unreadable").Should().Be(misses);
    }

    [Fact]
    public async Task A_regenerated_hash_keeps_the_fields_it_writes_over_when_every_field_was_readable()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, FastX);
        await raw.GetDatabase().HashDeleteAsync(redisKey, "a");
        await raw.GetDatabase().HashSetAsync(redisKey, "c", Array.Empty<byte>());

        await hashes.GetOrAddAsync<string>(key, _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["a"] = "generated" }), DateTimeOffset.UtcNow.AddMinutes(5), HashCacheSetOption.HashReplace, policy, Ct);

        (await raw.GetDatabase().HashExistsAsync(redisKey, "c")).Should().BeTrue("nothing was unreadable, so the caller's HashReplace is honored");
    }

    [Fact]
    public void Peer_fields_are_not_decoded_without_a_policy_even_when_the_default_policy_lists_formats()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build(defaultPolicy: Policy("fast", ["json", "fast"]), named: false);
        var sut = (RedisHashCache)provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = FastX }, policy: null, out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_version_field_is_not_a_serialized_value_so_no_list_applies_to_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, "fastonly");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedHashAsync(hashes, raw, FastX);
        await raw.GetDatabase().HashSetAsync(redisKey, "_version_", "7");

        var entry = await hashes.GetCacheEntryAsync<string>(key, policy, Ct);

        entry.Found.Should().BeTrue();
        entry.Value.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
        HashVersions.NumberOf(entry).Should().Be(7);
    }

    [Theory]
    [InlineData("fastonly", true, false)]
    [InlineData("fastonly", false, true)]
    [InlineData("jsononly", true, true)]
    [InlineData("jsononly", false, false)]
    [InlineData("open", true, true)]
    public async Task Peer_fields_outside_the_read_list_are_not_applied(string name, bool json, bool applied)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var (hashes, policy) = HashesAndPolicy(provider, name);
        var sut = (RedisHashCache)hashes;

        var decoded = sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = json ? JsonX : FastX }, policy, out var values);

        decoded.Should().Be(applied);
        if (applied)
        {
            values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
        }
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static CachePolicy Policy(string write, string[]? read) =>
        new() { DistributedExpiration = TimeSpan.FromMinutes(5), Serializer = new SerializerPolicy { Write = write, Read = read } };

    private static async Task<string?> ReadAsync(ICache cache, string operation, string key, CachePolicy policy)
    {
        switch (operation)
        {
            case "get":
                return await cache.GetAsync<string>(key, policy, Ct);
            case "entry":
                return (await cache.GetCacheEntryAsync<string>(key, policy, Ct)).Value;
            case "batch":
                return (await cache.GetAsync<string>([key], policy, Ct)).Single().Value;
            case "getoradd":
                return await cache.GetOrAddAsync<string>(key, _ => Task.FromResult<string?>("generated"), policy, Ct);
            default:
                return await ((RedisCache)cache).GetOrAddWithExpirationAsync<string>(key, _ => Task.FromResult(new GeneratedValue<string>("generated")), policy, Ct);
        }
    }

    private static async Task<string?> ReadHashAsync(IHashCache hashes, string operation, string key, CachePolicy policy)
    {
        switch (operation)
        {
            case "all":
                return FieldA(await hashes.GetAsync<string>(key, policy, Ct));
            case "item":
                return await hashes.GetItemAsync<string>(key, "a", policy, Ct);
            case "fields":
                return FieldA(await hashes.GetAsync<string>(key, ["a"], policy, Ct));
            case "entry":
                return FieldA((await hashes.GetCacheEntryAsync<string>(key, policy, Ct)).Value);
            default:
                var generated = new Dictionary<string, string?> { ["a"] = "generated" };
                return FieldA(await hashes.GetOrAddAsync<string>(key, _ => Task.FromResult<IDictionary<string, string?>>(generated), TimeSpan.FromMinutes(5), policy, Ct));
        }
    }

    private static string? FieldA(IDictionary<string, string?>? fields) => fields is not null && fields.TryGetValue("a", out var value) ? value : null;

    private static (ICache Cache, CachePolicy Policy) CacheAndPolicy(ServiceProvider provider, string name) =>
        (provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis), provider.GetRequiredService<ICachePolicyFactory>().Resolve(name)!);

    private static (IHashCache Hashes, CachePolicy Policy) HashesAndPolicy(ServiceProvider provider, string name) =>
        (provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis), provider.GetRequiredService<ICachePolicyFactory>().Resolve(name)!);

    private static RedisKey RawKey(ConnectionMultiplexer raw, string key) =>
        raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    private static async Task<byte[]?> RawValueAsync(ConnectionMultiplexer raw, string key) =>
        await raw.GetDatabase().StringGetAsync(RawKey(raw, key));

    private static async Task<(string Key, RedisKey RedisKey)> SeedAsync(ICache cache, ConnectionMultiplexer raw, byte[] stored)
    {
        var key = Unique();
        await cache.SetAsync(key, "seed", policy: null, Ct);
        var redisKey = RawKey(raw, key);
        await raw.GetDatabase().StringSetAsync(redisKey, stored, TimeSpan.FromMinutes(5));
        return (key, redisKey);
    }

    private static async Task<(string Key, RedisKey RedisKey)> SeedHashAsync(IHashCache hashes, ConnectionMultiplexer raw, byte[] stored)
    {
        var key = Unique();
        await hashes.SetAsync(key, new Dictionary<string, string?> { ["a"] = "seed" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy: null, Ct);
        var redisKey = RawKey(raw, key);
        await raw.GetDatabase().HashSetAsync(redisKey, "a", stored);
        return (key, redisKey);
    }

    private ServiceProvider Build(
        CachePolicy? defaultPolicy = null,
        RecordingTelemetryProvider? telemetry = null,
        bool named = true,
        FormatNegotiationMode mode = FormatNegotiationMode.Force,
        Action<CacheOptions>? configure = null)
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
                    b.AddRedis(o => o.FormatNegotiation.Mode = mode);
                    b.AddValueSerializer("fast", _ => _serializer);
                },
                o =>
                {
                    o.AppShortName = "readlistit";
                    o.DefaultCachePolicy = defaultPolicy;
                    if (named)
                    {
                        o.Policies["fastonly"] = Policy("fast", ["fast"]);
                        o.Policies["jsononly"] = Policy("json", ["json"]);
                        o.Policies["both"] = Policy("fast", ["json", "fast"]);
                        o.Policies["open"] = Policy("fast", null);
                    }

                    configure?.Invoke(o);
                })
            .BuildServiceProvider();
    }
}
