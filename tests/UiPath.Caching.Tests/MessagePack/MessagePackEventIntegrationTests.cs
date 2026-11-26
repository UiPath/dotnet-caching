using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.MessagePack;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MessagePackEventIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"me{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    public enum Events
    {
        PlainJson,
        WriteJson,
        WriteBinary,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_applies_a_field_update_carried_by_a_binary_event()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var writer = Hashes(await Node("a", Events.WriteBinary));
        var peer = Hashes(await Node("b", Events.WriteBinary));
        var key = Unique();
        (await writer.SetAsync(key, Fields(("a", "1")), TimeSpan.FromMinutes(10), Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().HaveCount(1);
        using var observed = await BroadcastNodeSignals.AttachAsync(_nodes[1], key);

        (await writer.SetAsync(key, Fields(("b", "2")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(10), SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();

        await observed.TickAsync();
        await observed.ReceivedAsync(peer);
        (await peer.GetAsync<string>(key, Ct)).ContainsKey("b").Should().BeTrue("the peer applies the field from the binary event");
        (await peer.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        (await StreamBodies()).Should().Contain(body => body[0] == MessagePackFormats.Event, "the events on the wire are binary");
    }

    [Fact]
    public async Task A_node_that_reads_both_applies_a_binary_event_and_one_that_cannot_expires_its_copies()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Cache(await Node("a", Events.WriteBinary));
        var reader = Cache(await Node("b", Events.WriteBinary));
        var stale = Cache(await Node("c", Events.PlainJson));
        var key = Unique();
        await writer.SetAsync(key, "first", policy: null, Ct);
        (await reader.GetAsync<string>(key, policy: null, Ct)).Should().Be("first");
        (await stale.GetAsync<string>(key, policy: null, Ct)).Should().Be("first");
        using var observed = await BroadcastNodeSignals.AttachAsync(_nodes[1], key);

        using var staleObserved = _nodes.Count == 3 ? await BroadcastNodeSignals.AttachAsync(_nodes[2], key) : null;
        await writer.SetAsync(key, "second", policy: null, Ct);
        if (!_nodes[1].GetRequiredService<RedisPubSubTopicProvider>().Enabled)
        {
            await observed.TickAsync();
            if (staleObserved is not null)
            {
                await staleObserved.TickAsync();
            }
        }
        await observed.ReceivedAsync(reader);
        if (staleObserved is not null)
        {
            await staleObserved.ReceivedAsync(stale);
        }

        (await reader.GetAsync<string>(key, policy: null, Ct) == "second").Should().BeTrue("a node that reads both applies the event");
        (await stale.GetAsync<string>(key, policy: null, Ct) == "second").Should().BeTrue("a node that cannot read it treats the event as a loss and drops its copies");
    }

    [Fact]
    public async Task Over_pub_sub_a_node_that_reads_both_applies_a_binary_event_and_one_that_cannot_expires_its_copies_though_it_keeps_its_tier_over_a_gap()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var writer = Cache(await Node("a", Events.WriteBinary, pubSub: true));
        var reader = Cache(await Node("b", Events.WriteBinary, pubSub: true));
        var stale = Cache(await Node("c", Events.PlainJson, pubSub: true));
        var key = Unique();
        await writer.SetAsync(key, "first", policy: null, Ct);
        (await reader.GetAsync<string>(key, policy: null, Ct)).Should().Be("first");
        (await stale.GetAsync<string>(key, policy: null, Ct)).Should().Be("first");
        using var observed = await BroadcastNodeSignals.AttachAsync(_nodes[1], key);

        using var staleObserved = _nodes.Count == 3 ? await BroadcastNodeSignals.AttachAsync(_nodes[2], key) : null;
        await writer.SetAsync(key, "second", policy: null, Ct);
        if (!_nodes[1].GetRequiredService<RedisPubSubTopicProvider>().Enabled)
        {
            await observed.TickAsync();
            if (staleObserved is not null)
            {
                await staleObserved.TickAsync();
            }
        }
        await observed.ReceivedAsync(reader);
        if (staleObserved is not null)
        {
            await staleObserved.ReceivedAsync(stale);
        }

        (await reader.GetAsync<string>(key, policy: null, Ct) == "second").Should().BeTrue("a node that reads both applies the event");
        (await stale.GetAsync<string>(key, policy: null, Ct) == "second").Should().BeTrue("a node that cannot read it treats the event as a loss, which a cache that ignores gaps still honours");
    }

    [Theory]
    [InlineData(true, Events.WriteBinary, false)]
    [InlineData(false, Events.WriteBinary, false)]
    [InlineData(true, Events.WriteJson, false)]
    [InlineData(false, Events.WriteJson, false)]
    [InlineData(true, Events.WriteBinary, true)]
    [InlineData(false, Events.WriteJson, true)]
    public async Task With_cloud_events_in_either_order_a_peer_decodes_the_events_that_are_published(bool cloudEventsFirst, Events events, bool pubSub)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(!pubSub && !fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var writer = Cache(await Node("a", events, pubSub: pubSub, cloudEventsFirst: cloudEventsFirst));
        var reader = Cache(await Node("b", events, pubSub: pubSub, cloudEventsFirst: cloudEventsFirst));
        var key = Unique();
        await writer.SetAsync(key, "first", policy: null, Ct);
        (await reader.GetAsync<string>(key, policy: null, Ct)).Should().Be("first");
        using var observed = await BroadcastNodeSignals.AttachAsync(_nodes[1], key);

        await writer.SetAsync(key, "second", policy: null, Ct);
        if (!_nodes[1].GetRequiredService<RedisPubSubTopicProvider>().Enabled)
        {
            await observed.TickAsync();
        }
        await observed.ReceivedAsync(reader);

        (await reader.GetAsync<string>(key, policy: null, Ct) == "second").Should().BeTrue("the peer decoded the event that was published");
        if (!pubSub)
        {
            var bodies = await StreamBodies();
            bodies.Should().NotBeEmpty();
            if (events == Events.WriteBinary)
            {
                bodies.Should().Contain(body => body[0] == MessagePackFormats.Event);
            }
            else
            {
                bodies.Should().NotContain(body => body[0] == MessagePackFormats.Event);
                bodies.Select(System.Text.Encoding.UTF8.GetString).Should().Contain(body => body.Contains("\"source\":"), "the JSON wire is the CloudEvents one");
            }
        }
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static ICache Cache(ServiceProvider node) =>
        node.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);

    private static IHashCache Hashes(ServiceProvider node) =>
        node.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);

    private async Task<List<byte[]>> StreamBodies()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = redis.GetDatabase();
        var bodies = new List<byte[]>();
        foreach (var key in RedisTestKeys.Keys(redis, $"*{_app}*"))
        {
            if (await database.KeyTypeAsync(key) != RedisType.Stream)
            {
                continue;
            }

            foreach (var entry in await database.StreamRangeAsync(key))
            {
                bodies.AddRange(entry.Values.Select(v => (byte[]?)v.Value).Where(v => v is { Length: > 0 })!);
            }
        }

        return bodies;
    }

    private async Task<ServiceProvider> Node(string source, Events events, bool pubSub = false, bool? cloudEventsFirst = null)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var time = (RedisResult[])(await redis.GetDatabase().ExecuteAsync("TIME"))!;
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds((long)time[0] * 1000 + (long)time[1] / 1000));
        var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ICachingTelemetryProvider, RecordingTelemetryProvider>()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    // These tests are about the wire; the automatic switch has its own.
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    if (pubSub)
                    {
                        b.AddRedisPubSub(o => o.Enabled = true);
                        b.AddRedisStreams(o => { o.Enabled = false; o.MaintainerEnabled = false; });
                    }

                    b.AddRedisStreams(o => { o.NotifyEnabled = false; o.MaintainerEnabled = false; });
                    b.AddRedisPubSub(o => o.SubscriberDueTime = TimeSpan.Zero);
                    b.AddBroadcast();
                    if (cloudEventsFirst == true)
                    {
                        b.AddCloudEvents();
                    }

                    b.AddInMemoryRedis(o =>
                    {
                        o.PeerFieldUpdates = true;
                        // A publication is not kept for a node that has not subscribed yet, and a gap clears the tier that proves nothing here.
                        o.ClearLocalOnReconnect = pubSub ? false : null;
                    });
                    if (events != Events.PlainJson)
                    {
                        b.AddMessagePackEvents(write: events == Events.WriteBinary);
                    }

                    if (cloudEventsFirst == false)
                    {
                        b.AddCloudEvents();
                    }
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                    if (pubSub)
                    {
                        o.DefaultTopic = KnownTopicNames.RedisPubSub;
                    }
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        StreamsReady.Await(provider);
        return provider;
    }
}
