using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.MessagePack;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MessagePackIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private const string Policy = "mp";

    private readonly string _app = $"mp{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Key_switches_formats_over_time()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(Node("a"), KnownCacheProviderNames.Redis);
        var mp = PolicyOf(_nodes[0]);
        var key = Unique();

        await cache.SetAsync(key, new Profile("json", 1), policy: null, Ct);
        (await FirstByte(key)).Should().Be((byte)'{');
        (await cache.GetAsync<Profile>(key, mp, Ct)).Should().Be(new Profile("json", 1));

        await cache.SetAsync(key, new Profile("msgpack", 2), mp, Ct);
        (await FirstByte(key)).Should().Be(CacheValueFormats.MessagePack);
        (await cache.GetAsync<Profile>(key, policy: null, Ct)).Should().Be(new Profile("msgpack", 2));

        await cache.SetAsync(key, new Profile("json again", 3), policy: null, Ct);
        (await FirstByte(key)).Should().Be((byte)'{');
        (await cache.GetAsync<Profile>(key, mp, Ct)).Should().Be(new Profile("json again", 3));
    }

    [Fact]
    public async Task A_large_compressible_value_is_stored_compressed_and_reads_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(Node("a"), KnownCacheProviderNames.Redis);
        var key = Unique();
        var value = new Profile(new string('x', 50_000), 7);

        await cache.SetAsync(key, value, PolicyOf(_nodes[0]), Ct);

        (await FirstByte(key)).Should().Be(CacheValueFormats.MessagePackLz4);
        (await cache.GetAsync<Profile>(key, policy: null, Ct)).Should().Be(value);
    }

    [Fact]
    public async Task Multilayer_peer_invalidation_works_with_msgpack()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Cache(Node("a"), KnownCacheProviderNames.InMemoryRedis);
        var reader = Cache(Node("b"), KnownCacheProviderNames.InMemoryRedis);
        var mp = PolicyOf(_nodes[0]);
        var key = Unique();
        await writer.SetAsync(key, new Profile("first", 1), mp, Ct);
        (await reader.GetAsync<Profile>(key, mp, Ct)).Should().Be(new Profile("first", 1));

        await writer.SetAsync(key, new Profile("second", 2), mp, Ct);

        (await UntilAsync(async () => await reader.GetAsync<Profile>(key, mp, Ct) == new Profile("second", 2)))
            .Should().BeTrue("the peer's invalidation drops the reader's copy and it reads the new value from Redis");
    }

    [Fact]
    public async Task Hash_cache_round_trips_with_msgpack()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a");
        var hashes = Hashes(node, KnownCacheProviderNames.Redis);
        var mp = PolicyOf(node);
        var key = Unique();
        var fields = new Dictionary<string, Profile?> { ["a"] = new("one", 1), ["b"] = new(new string('y', 5_000), 2) };
        var options = new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        (await hashes.SetAsync(key, fields, options, mp, Ct)).Should().BeTrue();
        var entry = await hashes.GetCacheEntryAsync<Profile>(key, mp, Ct);

        entry.Value.Should().BeEquivalentTo(fields);
        entry.Metadata.Should().BeEquivalentTo(options.Metadata);
        (await RawField(key, "a"))![0].Should().Be(CacheValueFormats.MessagePack);
        (await RawField(key, "b"))![0].Should().Be(CacheValueFormats.MessagePackLz4);
    }

    [Fact]
    public async Task Batch_set_and_get_with_msgpack()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a");
        var cache = Cache(node, KnownCacheProviderNames.Redis);
        var mp = PolicyOf(node);
        CacheKey[] keys = [Unique(), Unique(), Unique()];
        Profile[] values = [new("small", 1), new(new string('z', 4_000), 2), new("other", 3)];

        await cache.SetAsync([.. keys.Zip(values, (k, v) => new KeyValuePair<CacheKey, Profile?>(k, v))], mp, Ct);

        (await FirstByte(keys[0])).Should().Be(CacheValueFormats.MessagePack);
        (await FirstByte(keys[1])).Should().Be(CacheValueFormats.MessagePackLz4);
        (await cache.GetAsync<Profile>(keys, mp, Ct)).Select(v => v.Value).Should().Equal(values);
        (await cache.GetAsync<Profile>(keys, policy: null, Ct)).Select(v => v.Value).Should().Equal(values);
    }

    [Fact]
    public async Task Peer_field_update_with_msgpack_is_applied()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Hashes(Node("a", peerFieldUpdates: true, defaultPolicy: true), KnownCacheProviderNames.InMemoryRedis);
        var peer = Hashes(Node("b", peerFieldUpdates: true, defaultPolicy: true), KnownCacheProviderNames.InMemoryRedis);
        var key = Unique();
        (await writer.SetAsync(key, Fields(("a", "1")), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        (await peer.GetAsync<Profile>(key, Ct)).Should().HaveCount(1);
        await Task.Delay(1000, Ct);

        (await writer.SetAsync(key, Fields(("b", "2")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        (await UntilAsync(async () => (await peer.GetAsync<Profile>(key, Ct)).ContainsKey("b"))).Should().BeTrue("the peer applies the field from the event");
        (await peer.GetAsync<Profile>(key, Ct)).Keys.Should().BeEquivalentTo(["a", "b"]);
        (await RawField(key, "b"))![0].Should().Be(CacheValueFormats.MessagePack);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static ICache Cache(ServiceProvider node, string provider) =>
        node.GetRequiredService<ICacheFactory>().CreateCache(provider);

    private static IHashCache Hashes(ServiceProvider node, string provider) =>
        node.GetRequiredService<ICacheFactory>().CreateHashCache(provider);

    private static CachePolicy PolicyOf(ServiceProvider node) => node.GetRequiredService<ICachePolicyFactory>().Resolve(Policy)!;

    private static Dictionary<string, Profile?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (Profile?)new Profile(f.Value, int.Parse(f.Value, System.Globalization.CultureInfo.InvariantCulture)));

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50, Ct);
        }

        return false;
    }

    private async Task<byte> FirstByte(string key)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var found = redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
        return ((byte[])(await redis.GetDatabase().StringGetAsync(found))!)[0];
    }

    private async Task<byte[]?> RawField(string key, string field)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var found = redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
        return await redis.GetDatabase().HashGetAsync(found, field);
    }

    private ServiceProvider Node(string source, bool peerFieldUpdates = false, bool defaultPolicy = false)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.PeerFieldUpdates = peerFieldUpdates);
                    b.AddMessagePackSerializer();
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                    var policy = new CachePolicy
                    {
                        DistributedExpiration = TimeSpan.FromMinutes(5),
                        Serializer = new SerializerPolicy { Write = MessagePackCachingBuilderExtensions.DefaultName },
                    };
                    o.Policies[Policy] = policy;
                    if (defaultPolicy)
                    {
                        o.DefaultCachePolicy = policy;
                    }
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }

    public sealed record Profile(string Name, int Version);
}
