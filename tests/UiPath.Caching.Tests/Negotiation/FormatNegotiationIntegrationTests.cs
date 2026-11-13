using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Negotiation;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FormatNegotiationIntegrationTests(RedisContainerFixture fixture)
{
    private const string Policy = "fast";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_scripts_stamp_with_the_servers_clock_under_the_connectors_prefix_and_prune_by_stamp()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var redisOptions = new RedisCacheOptions { KeyPrefix = "a:" };
        var key = NodeListStore.KeyFor(new CacheOptions { AppShortName = $"fn{Guid.NewGuid():N}"[..12] }, redisOptions);
        var store = new NodeListStore(new PrefixingConnector(mux, redisOptions.KeyPrefix), key);
        var ad = new NodeAdvertisement("3.2.0", "i", ["json", "10"], new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["json"] });
        var db = mux.GetDatabase();
        try
        {
            var before = ServerMs(await db.ExecuteAsync("TIME"));
            await store.WriteAsync("node-a", ad, Ct);
            await store.WriteAsync("node-b", ad, Ct);
            await Task.Delay(1100, Ct);
            var after = ServerMs(await db.ExecuteAsync("TIME"));

            var read = await store.ReadAsync(Ct);

            // The stamp is the server's TIME, taken inside the script, and the age is the server's TIME again.
            read.Should().HaveCount(2);
            read[0].Stamp.Should().BeInRange(before, after);
            read[0].Age.Should().BeGreaterThan(TimeSpan.FromSeconds(1)).And.BeLessThan(TimeSpan.FromSeconds(10));
            read.Single(a => a.NodeId == "node-a").Advertisement.Strict["p"].Should().Equal("json");
            (await db.HashExistsAsync($"a:{key}", "node-a")).Should().BeTrue("the connector's prefix is the one the key lives under");

            // node-b advertises again after it was read: pruning by the stamp it was read with must leave it alone.
            var stale = read.Where(a => a.NodeId is "node-a" or "node-b").ToList();
            await store.WriteAsync("node-b", ad with { Left = true }, Ct);
            await store.PruneAsync(stale, Ct);

            var left = await store.ReadAsync(Ct);
            left.Select(a => a.NodeId).Should().Equal("node-b");
            left[0].Advertisement.Left.Should().BeTrue();
        }
        finally
        {
            await db.KeyDeleteAsync($"a:{key}");
        }
    }

    [Fact]
    public async Task With_redis_unreachable_the_store_stays_inside_its_budget()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "127.0.0.1:1,connectTimeout=30000", EnableHangDetection = false });
        using var connector = new RedisConnector(
            NullTelemetryProvider.Instance,
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance),
            options);
        var store = new NodeListStore(connector, "ads");
        var ad = new NodeAdvertisement("3.2.0", "i", ["json"], new Dictionary<string, IReadOnlyList<string>>());
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;

        var write = () => store.WriteAsync("node", ad, budget.Token);

        await write.Should().ThrowAsync<Exception>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5), "the lazy connect must not be waited on past the budget");
    }

    [Fact]
    public async Task A_stream_that_does_not_exist_has_no_groups_and_an_idle_group_reports_its_idle()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var source = new RedisStreamGroupSource(new PrefixingConnector(mux, null));
        RedisKey stream = $"fn-{Guid.NewGuid():N}";
        var db = mux.GetDatabase();
        try
        {
            (await source.GroupsAsync(stream, Ct)).Should().BeEmpty();

            await db.StreamAddAsync(stream, "f", "v");
            await db.StreamCreateConsumerGroupAsync(stream, "g", "0-0");
            (await source.IdleAsync(stream, "g", Ct)).Should().BeNull("a group nobody has read has no consumer");
            await db.StreamReadGroupAsync(stream, "g", "c", ">", 1);
            await Task.Delay(300, Ct);

            (await source.GroupsAsync(stream, Ct)).Should().Equal("g");
            (await source.IdleAsync(stream, "g", Ct)).Should().BeGreaterThan(TimeSpan.FromMilliseconds(200)).And.BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await db.KeyDeleteAsync(stream);
        }
    }

    [Fact]
    public async Task An_old_node_keeps_the_writer_on_json_until_it_goes_idle_and_a_returning_one_stops_the_writes_at_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = $"fn{Guid.NewGuid():N}"[..12];
        var topic = new TopicKey($"orders-{Guid.NewGuid():N}");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var node = Node(app, "node-a");
        using var oldNode = Node(app, "node-old", registerSerializer: false);
        var service = node.GetServices<IHostedService>().OfType<FormatNegotiationService>().Single();
        var negotiation = node.GetRequiredService<FormatNegotiation>();
        var policy = node.GetRequiredService<ICachePolicyFactory>().Resolve(Policy)!;
        var cache = node.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var hashes = node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var oldCache = oldNode.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var oldHashes = oldNode.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, new CacheOptions { AppShortName = app }).GetRedisKey(topic);
        var nodeListKey = NodeListStore.KeyFor(new CacheOptions { AppShortName = app }, new RedisCacheOptions());
        var writer = negotiation.Negotiator!;
        var serializer = node.GetRequiredService<ValueSerializerRegistry>().Resolve("fast")!;
        try
        {
            // The simulated old node: a consumer group named by its source id, read just now, and no advertisement.
            await db.StreamAddAsync(streamKey, "f", "v");
            await db.StreamCreateConsumerGroupAsync(streamKey, "urn:node-old", "0-0");
            await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);

            node.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(topic);
            await service.StartAsync(Ct);

            // The old node keeps reading for the length of a hold and a liveness window, so it stays live throughout.
            for (var i = 0; i < 10; i++)
            {
                await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);
                await Task.Delay(500, Ct);
            }

            writer.Allows(serializer).Should().BeFalse("a live group without an advertisement may not read the format");
            await cache.SetAsync("k1", "json-while-blocked", TimeSpan.FromMinutes(1), policy, Ct);
            var blocked = await RawAsync(db, app, "k1");
            blocked.Should().NotBeNull();
            blocked![0].Should().NotBe(0x10);

            // The old node stops reading: past the liveness window, and then past the hold with the same nodes, the writer switches.
            await WaitAsync(() => writer.Allows(serializer), TimeSpan.FromSeconds(30));
            await cache.SetAsync("k2", "headered", TimeSpan.FromMinutes(1), policy, Ct);
            (await RawAsync(db, app, "k2"))![0].Should().Be(0x10);
            await hashes.SetAsync("h2", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(1), Metadata: new Dictionary<string, string?> { ["m"] = "v" }), policy, Ct);
            (await db.HashGetAsync(HashKey(raw, app, "h2"), "a")).ToString().Should().StartWith("\u0010");

            // Peer fields travel as the bytes the hash holds, so they follow the same decision.
            var versioned = (RedisHashCache)hashes;
            var peer = await versioned.SetVersionedAsync("h2p", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(1), SetOption: HashCacheSetOption.HashReplace), policy, new PeerFieldLimits(10, 1024), Ct);
            peer.Fields!["a"]![0].Should().Be(0x10);

            // An old node that joined in the window reads the new value as a miss, never as another value.
            (await oldCache.GetAsync<string>("k2", policy: null, Ct)).Should().BeNull();
            (await oldHashes.GetAsync<string>("h2", policy: null, Ct)).Should().BeNullOrEmpty();

            // It comes back: fresh read, no advertisement. The next view stops the writes, without a hold.
            await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);
            await WaitAsync(() => !writer.Allows(serializer), TimeSpan.FromSeconds(5));
            var peerAfter = await versioned.SetVersionedAsync("h3p", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(1), SetOption: HashCacheSetOption.HashReplace), policy, new PeerFieldLimits(10, 1024), Ct);
            peerAfter.Fields!["a"]![0].Should().NotBe(0x10);
            await cache.SetAsync("k3", "json-again", TimeSpan.FromMinutes(1), policy, Ct);
            await hashes.SetAsync("h3", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(1)), policy, Ct);

            (await oldCache.GetAsync<string>("k3", policy: null, Ct)).Should().Be("json-again");
            (await oldHashes.GetAsync<string>("h3", policy: null, Ct)).Should().ContainKey("a").WhoseValue.Should().Be("x");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await db.KeyDeleteAsync([streamKey, nodeListKey]);
        }
    }

    [Fact]
    public async Task A_node_that_stops_gracefully_never_makes_the_rest_fall_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = $"fn{Guid.NewGuid():N}"[..12];
        var topic = new TopicKey($"orders-{Guid.NewGuid():N}");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var stays = Node(app, "node-stays");
        using var leaves = Node(app, "node-leaves");
        var staysService = stays.GetServices<IHostedService>().OfType<FormatNegotiationService>().Single();
        var leavesService = leaves.GetServices<IHostedService>().OfType<FormatNegotiationService>().Single();
        var writer = stays.GetRequiredService<FormatNegotiation>().Negotiator!;
        var serializer = stays.GetRequiredService<ValueSerializerRegistry>().Resolve("fast")!;
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, new CacheOptions { AppShortName = app }).GetRedisKey(topic);
        var nodeListKey = NodeListStore.KeyFor(new CacheOptions { AppShortName = app }, new RedisCacheOptions());
        try
        {
            stays.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(topic);
            leaves.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(topic);
            await staysService.StartAsync(Ct);
            await leavesService.StartAsync(Ct);
            await WaitAsync(() => writer.Allows(serializer), TimeSpan.FromSeconds(30));

            // Its group is read a moment ago and its ad is gone from the readers: both are the departed's, and neither may count against the fleet.
            await leavesService.StopAsync(Ct);
            leaves.Dispose();
            for (var i = 0; i < 8; i++)
            {
                writer.Allows(serializer).Should().BeTrue("a node that left is accounted for, not an unaccounted one");
                await Task.Delay(500, Ct);
            }
        }
        finally
        {
            await staysService.StopAsync(CancellationToken.None);
            await db.KeyDeleteAsync([streamKey, nodeListKey]);
        }
    }

    private static long ServerMs(RedisResult time)
    {
        var parts = (RedisResult[])time!;
        return ((long)parts[0] * 1000) + ((long)parts[1] / 1000);
    }

    private static async Task<byte[]?> RawAsync(IDatabase db, string app, string key)
    {
        var server = db.Multiplexer.GetServer(db.Multiplexer.GetEndPoints()[0]);
        var found = server.Keys(pattern: $"{app}:s:{key}").Cast<RedisKey?>().FirstOrDefault();
        return found is null ? null : (byte[]?)await db.StringGetAsync(found.Value);
    }

    private static RedisKey HashKey(ConnectionMultiplexer raw, string app, string key) =>
        raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"{app}:h:{key}").Single();

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not hold in time.");
            await Task.Delay(200, Ct);
        }
    }

    private ServiceProvider Node(string app, string source, bool registerSerializer = true) =>
        new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.AdvertiseInterval = TimeSpan.FromSeconds(1);
                        o.FormatNegotiation.ViewInterval = TimeSpan.FromSeconds(1);
                        o.FormatNegotiation.LivenessWindow = TimeSpan.FromSeconds(3);
                        o.FormatNegotiation.UpgradeHold = TimeSpan.FromSeconds(2);
                        o.FormatNegotiation.MaxViewAge = TimeSpan.FromSeconds(10);
                    });
                    b.AddRedisStreams(o => { o.Enabled = true; o.MaintainerEnabled = false; });
                    b.AddBroadcast();
                    b.AddInMemoryRedis(_ => { });
                    if (registerSerializer)
                    {
                        b.AddValueSerializer("fast", _ => new PassthroughValueSerializer(0x10));
                    }
                },
                o =>
                {
                    o.AppShortName = app;
                    o.SourceUri = new Uri($"urn:{source}");
                    if (registerSerializer)
                    {
                        o.Policies[Policy] = new CachePolicy
                        {
                            DistributedExpiration = TimeSpan.FromMinutes(5),
                            Serializer = new SerializerPolicy { Write = "fast" },
                        };
                    }
                })
            .BuildServiceProvider();

    private sealed class PrefixingConnector(IConnectionMultiplexer mux, string? prefix) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add { }
            remove { }
        }

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool IsConnected => mux.IsConnected;

        public Version Version => mux.GetServer(mux.GetEndPoints()[0]).Version;

        public IDatabase Database => prefix is null ? mux.GetDatabase() : mux.GetDatabase().WithKeyPrefix(prefix);

        public ISubscriber Subscriber => mux.GetSubscriber();

        public void ForceReconnect()
        {
        }

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => mux.GetEndPoints(configuredOnly);

        public void Dispose()
        {
        }
    }
}
