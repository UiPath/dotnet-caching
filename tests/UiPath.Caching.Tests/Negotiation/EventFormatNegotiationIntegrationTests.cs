using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Negotiation;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class EventFormatNegotiationIntegrationTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sum_over_all_endpoints_counts_every_connection_that_subscribed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var first = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await using var second = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await using var asking = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var channel = RedisChannel.Literal($"fe-{Guid.NewGuid():N}");
        await first.GetSubscriber().SubscribeAsync(channel, (_, _) => { });
        await second.GetSubscriber().SubscribeAsync(channel, (_, _) => { });
        var source = new RedisSubscriberSource(new Connector(asking));
        var nodeList = new PubSubNodeList(source);

        source.Endpoints().Should().NotBeEmpty();
        await nodeList.ReadAsync([channel.ToString()], new Dictionary<string, long> { [channel.ToString()] = 1 }, Ct);
        var reading = await nodeList.ReadAsync([channel.ToString()], new Dictionary<string, long> { [channel.ToString()] = 1 }, Ct);

        reading.Known.Should().BeTrue();
        reading.Surplus.Should().Be(1, "two connections subscribed and one is accounted for");
        reading.Unaccounted.Should().BeTrue();
        var other = RedisChannel.Literal($"fe-{Guid.NewGuid():N}");
        await first.GetSubscriber().SubscribeAsync(other, (_, _) => { });
        (await source.CountAsync(source.Endpoints()[0], [channel.ToString(), other.ToString(), $"fe-nobody-{Guid.NewGuid():N}"], Ct))!.Channels.Should().Equal(new long[] { 2, 1, 0 }, "one command, every channel, in the order asked");
    }

    [Fact]
    public async Task A_connection_with_a_channel_prefix_is_counted_under_the_names_it_really_subscribed_to()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var options = ConfigurationOptions.Parse(fixture.ConnectionString);
        options.ChannelPrefix = RedisChannel.Literal($"pfx-{Guid.NewGuid():N}:");
        await using var prefixed = await ConnectionMultiplexer.ConnectAsync(options);
        await using var another = await ConnectionMultiplexer.ConnectAsync(options);
        var channel = $"fe-{Guid.NewGuid():N}";
        await prefixed.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, _) => { });
        await another.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, _) => { });
        var source = new RedisSubscriberSource(new Connector(prefixed));

        var counted = await source.CountAsync(source.Endpoints()[0], [channel], Ct);

        // The server knows the channel only with the prefix in front. Asked as the bare name, there is nobody on it.
        counted!.Channels.Should().Equal(new long[] { 2 }, "two connections subscribed under the prefix, and the count is of those");
        await using var bare = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        (await new RedisSubscriberSource(new Connector(bare)).CountAsync(source.Endpoints()[0], [channel], Ct))!.Channels.Should().Equal(new long[] { 0 });
    }

    [Fact]
    public async Task A_pattern_subscriber_is_counted_by_the_server_and_claims_account_for_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var holder = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await using var asking = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var pattern = RedisChannel.Pattern($"fe-{Guid.NewGuid():N}-*");
        var channel = $"fe-{Guid.NewGuid():N}";
        await asking.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, _) => { });
        var source = new RedisSubscriberSource(new Connector(asking));
        var before = (await source.CountAsync(source.Endpoints()[0], [channel], Ct))!.Patterns;
        await holder.GetSubscriber().SubscribeAsync(pattern, (_, _) => { });
        var nodeList = new PubSubNodeList(source);
        var accounted = new Dictionary<string, long> { [channel] = 1, [PubSubNodeList.PatternKey] = before };

        await nodeList.ReadAsync([channel], accounted, Ct);
        var unadvertised = await nodeList.ReadAsync([channel], accounted, Ct);
        accounted[PubSubNodeList.PatternKey] = before + 1;
        await nodeList.ReadAsync([channel], accounted, Ct);
        var advertised = await nodeList.ReadAsync([channel], accounted, Ct);

        unadvertised.Unaccounted.Should().BeTrue("NUMSUB cannot see a pattern subscriber, NUMPAT can");
        unadvertised.Channels.Should().Equal(PubSubNodeList.PatternKey);
        advertised.Unaccounted.Should().BeFalse();
        advertised.Settled.Should().BeTrue();
    }

    [Fact]
    public async Task A_confirmed_subscription_is_already_counted_by_the_server()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var node = Node(app, "node-a", pubSub: true, write: true);
        var provider = (RedisPubSubTopicProvider)node.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisPubSub);
        var subscriptions = (IPubSubSubscriptions)provider;
        var channel = $"{app}:ps:string";
        provider.Create(new TopicKey("string"));
        subscriptions.Channels().Should().Equal(channel);
        subscriptions.Confirmed().Should().BeEmpty("the subscription goes in place on a timer, not when the topic is made");

        // Whenever the node would claim the subscription, the server must already count it: the claim never comes first.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (subscriptions.Confirmed().Count == 0)
        {
            Assert.True(DateTime.UtcNow < until, "The subscription was not confirmed in time.");
            await Task.Delay(1, Ct);
        }

        raw.GetServer(raw.GetEndPoints()[0]).SubscriptionSubscriberCount(RedisChannel.Literal(channel)).Should().Be(1);
        subscriptions.Confirmed().Should().BeEquivalentTo(new Dictionary<string, int> { [channel] = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pattern_or_sharded_channel_is_listed_as_uncountable_and_never_claimed(bool sharded)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var node = Node(app, "node-a", pubSub: true, write: true, new ChannelStrategy(app, sharded));
        var provider = (RedisPubSubTopicProvider)node.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisPubSub);
        var subscriptions = (IPubSubSubscriptions)provider;

        provider.Create(new TopicKey("string"));

        subscriptions.Channels().Should().BeEmpty("PUBSUB NUMSUB counts classic literal channels only");
        subscriptions.Uncountable().Should().Equal($"{app}:ps:string");
        subscriptions.Confirmed().Should().BeEmpty();
    }

    [Fact]
    public async Task A_pub_sub_subscriber_that_advertises_nothing_keeps_events_json_until_it_leaves_and_returns_them_to_json_when_it_comes_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        var channel = RedisChannel.Literal($"{app}:ps:string");
        var topic = new TopicKey("string");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var a = Node(app, "node-a", pubSub: true, write: true);
        using var b = Node(app, "node-b", pubSub: true, write: true);
        var serviceA = Service(a);
        var serviceB = Service(b);
        var writes = a.GetRequiredService<IBinaryEvents>();
        var cache = a.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var received = new ConcurrentQueue<byte[]>();
        var nodeListKey = NodeListStore.KeyFor(new CacheOptions { AppShortName = app }, new RedisCacheOptions());
        try
        {
            a.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisPubSub).Create(topic);
            b.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisPubSub).Create(topic);
            await serviceA.StartAsync(Ct);
            await serviceB.StartAsync(Ct);

            // Both nodes subscribed and said so: nothing is unaccounted for, so after the hold the events are binary.
            await WaitAsync(() => writes.Writes, TimeSpan.FromSeconds(40));
            await raw.GetSubscriber().SubscribeAsync(channel, (_, value) => received.Enqueue((byte[])value!));

            // A subscriber nobody advertised: two views, and the writes are JSON again.
            await WaitAsync(() => !writes.Writes, TimeSpan.FromSeconds(15));
            await cache.SetAsync("k1", "json-while-blocked", TimeSpan.FromMinutes(1), policy: null, Ct);
            await WaitAsync(() => received.Any(body => body[0] == (byte)'{'), TimeSpan.FromSeconds(10));
            received.Should().NotContain(body => body[0] == CacheValueFormats.MessagePack);

            // It leaves: two clean views and the hold, and they are binary.
            await raw.GetSubscriber().UnsubscribeAsync(channel);
            await WaitAsync(() => writes.Writes, TimeSpan.FromSeconds(40));
            received.Clear();
            await raw.GetSubscriber().SubscribeAsync(channel, (_, value) => received.Enqueue((byte[])value!));
            await cache.SetAsync("k2", "binary-until-it-returns", TimeSpan.FromMinutes(1), policy: null, Ct);
            await WaitAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(10));
            received.Should().Contain(body => body[0] == CacheValueFormats.MessagePack, "the returning subscriber is not yet seen, and a node that reads both loses nothing");

            // Seen in two views, it stops them again.
            await WaitAsync(() => !writes.Writes, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await serviceA.StopAsync(CancellationToken.None);
            await serviceB.StopAsync(CancellationToken.None);
            await db.KeyDeleteAsync(nodeListKey);
        }
    }

    [Fact]
    public async Task A_stream_group_that_advertises_nothing_keeps_events_json_until_it_goes_idle_and_a_returning_one_stops_the_binary_writes_at_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        var topic = new TopicKey("string");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var node = Node(app, "node-a", pubSub: false, write: true);
        var service = Service(node);
        var writes = node.GetRequiredService<IBinaryEvents>();
        var cache = node.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, new CacheOptions { AppShortName = app }).GetRedisKey(topic);
        var nodeListKey = NodeListStore.KeyFor(new CacheOptions { AppShortName = app }, new RedisCacheOptions());
        try
        {
            await db.StreamAddAsync(streamKey, "f", "v");
            await db.StreamCreateConsumerGroupAsync(streamKey, "urn:node-old", "0-0");
            await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);
            node.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(topic);
            await service.StartAsync(Ct);

            for (var i = 0; i < 8; i++)
            {
                await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);
                await Task.Delay(500, Ct);
            }

            writes.Writes.Should().BeFalse("a live group without an advertisement may not read binary events");
            await cache.SetAsync("k1", "json-while-blocked", TimeSpan.FromMinutes(1), policy: null, Ct);
            (await LastBodyAsync(db, streamKey))[0].Should().Be((byte)'{');

            await WaitAsync(() => writes.Writes, TimeSpan.FromSeconds(30));
            await cache.SetAsync("k2", "binary", TimeSpan.FromMinutes(1), policy: null, Ct);
            (await LastBodyAsync(db, streamKey))[0].Should().Be(CacheValueFormats.MessagePack);

            await db.StreamReadGroupAsync(streamKey, "urn:node-old", "urn:node-old", ">", 1);
            await WaitAsync(() => !writes.Writes, TimeSpan.FromSeconds(5));
            await cache.SetAsync("k3", "json-again", TimeSpan.FromMinutes(1), policy: null, Ct);
            (await LastBodyAsync(db, streamKey))[0].Should().Be((byte)'{');
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await db.KeyDeleteAsync([streamKey, nodeListKey]);
        }
    }

    private static async Task<byte[]> LastBodyAsync(IDatabase db, RedisKey stream)
    {
        // The topic publishes just after the call returns on a background thread; look until the entry is there.
        for (var i = 0; i < 50; i++)
        {
            var last = await db.StreamRangeAsync(stream, count: 1, messageOrder: Order.Descending);
            if (last.Length > 0 && last[0].Values.FirstOrDefault(v => v.Value.Length() > 1) is { Value.IsNull: false } body)
            {
                return (byte[])body.Value!;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("No event reached the stream.");
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not hold in time.");
            await Task.Delay(100, Ct);
        }
    }

    private static string App() => $"fe{Guid.NewGuid():N}"[..12];

    private static FormatNegotiationService Service(ServiceProvider node) =>
        node.GetServices<IHostedService>().OfType<FormatNegotiationService>().Single();

    private ServiceProvider Node(string app, string source, bool pubSub, bool write, IRedisChannelStrategy? strategy = null) =>
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
                    if (pubSub)
                    {
                        b.AddRedisPubSub(o =>
                        {
                            o.Enabled = true;
                            o.RedisChannelStrategy = strategy;
                        });
                        b.AddRedisStreams(o => { o.Enabled = false; o.MaintainerEnabled = false; });
                    }
                    else
                    {
                        b.AddRedisStreams(o => { o.Enabled = true; o.MaintainerEnabled = false; });
                    }

                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.ClearLocalOnReconnect = pubSub ? false : null);
                    b.AddMessagePackEvents(write);
                },
                o =>
                {
                    o.AppShortName = app;
                    o.SourceUri = new Uri($"urn:{source}");
                    if (pubSub)
                    {
                        o.DefaultTopic = KnownTopicNames.RedisPubSub;
                    }
                })
            .BuildServiceProvider();

    private sealed class ChannelStrategy(string app, bool sharded) : IRedisChannelStrategy
    {
        public RedisChannel GetRedisChannel(TopicKey topicKey) => sharded
            ? RedisChannel.Sharded($"{app}:ps:{topicKey}")
            : RedisChannel.Pattern($"{app}:ps:{topicKey}");
    }

    private sealed class Connector(IConnectionMultiplexer mux) : IRedisConnector
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

        public IDatabase Database => mux.GetDatabase();

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
