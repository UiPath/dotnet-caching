using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Broadcast;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RouterSeamsIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"rt{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(KnownTopicNames.RedisPubSub)]
    [InlineData(KnownTopicNames.RedisStreams)]
    public async Task A_public_subscriber_on_another_node_is_told_events_were_missed_when_a_topic_is_invalidated(string transport)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var publisherNode = Node("a", transport);
        var subscriberNode = Node("b", transport);
        var key = new TopicKey($"orders-{Guid.NewGuid():N}");
        var observer = new MissedObserver();
        var topic = Provider(subscriberNode, transport).Create(key);
        using var subscription = topic.Subscribe(observer);
        (await UntilAsync(() => State(topic).IsSubscribed)).Should().BeTrue();
        var publisher = new CacheEventPublisher(
            "router",
            Provider(publisherNode, transport),
            publisherNode.GetRequiredService<ICacheEventFactory>(),
            NullLogger<CacheEventPublisher>.Instance);

        (await publisher.TopicInvalidatedAsync(key)).Should().BeTrue();

        (await UntilAsync(() => observer.Reasons.Contains(MissedEventsReason.Lost))).Should().BeTrue("the other node's public invalidation expires every subscriber of the topic");
    }

    [Fact]
    public async Task A_pub_sub_topic_is_subscribed_once_the_server_has_the_subscription_and_not_after_it_is_disposed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", KnownTopicNames.RedisPubSub);
        var topic = Provider(node, KnownTopicNames.RedisPubSub).Create(new TopicKey($"orders-{Guid.NewGuid():N}"));
        var state = State(topic);

        state.IsSubscribed.Should().BeFalse("the subscription is made on the subscriber's first timer tick, which is seconds away");
        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var subscribers = (RedisResult[])(await redis.GetDatabase().ExecuteAsync("PUBSUB", "NUMSUB", $"{_app}:ps:{topic.TopicKey}"))!;
        ((long)subscribers[1]).Should().BeGreaterThan(0, "a subscribed topic is one the server would deliver to");

        topic.Dispose();

        state.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task A_streams_topic_is_subscribed_once_its_group_exists_and_the_reader_runs_and_not_after_it_is_disposed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", KnownTopicNames.RedisStreams);
        var topic = Provider(node, KnownTopicNames.RedisStreams).Create(new TopicKey($"orders-{Guid.NewGuid():N}"));
        var state = State(topic);

        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue();
        (await GroupsAsync()).Should().NotBeEmpty("a topic reports itself subscribed only with its consumer group in place");

        topic.Dispose();

        state.IsSubscribed.Should().BeFalse();
    }

    [Theory]
    [InlineData(KnownTopicNames.RedisPubSub)]
    [InlineData(KnownTopicNames.RedisStreams)]
    public async Task A_topic_with_no_server_to_reach_is_not_subscribed(string transport)
    {
        var node = Node("a", transport, "127.0.0.1:1,connectTimeout=300,abortConnect=false");
        var topic = Provider(node, transport).Create(new TopicKey("orders"));

        await Task.Delay(1500, Ct);

        State(topic).IsSubscribed.Should().BeFalse();
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    private static ITopicSubscriptionState State(ITopic<ICacheEvent> topic) =>
        topic.Should().BeAssignableTo<ITopicSubscriptionState>().Subject;

    private static ITopicProvider Provider(ServiceProvider node, string transport) =>
        node.GetRequiredService<ITopicFactory>().Get(transport);

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, Ct);
        }

        return false;
    }

    private async Task<List<StreamGroupInfo>> GroupsAsync()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = redis.GetDatabase();
        var groups = new List<StreamGroupInfo>();
        foreach (var key in redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"*{_app}*"))
        {
            if (await database.KeyTypeAsync(key) == RedisType.Stream)
            {
                groups.AddRange(await database.StreamGroupInfoAsync(key));
            }
        }

        return groups;
    }

    private ServiceProvider Node(string source, string transport, string? connectionString = null)
    {
        var pubSub = transport == KnownTopicNames.RedisPubSub;
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connectionString ?? fixture.ConnectionString);
                    b.AddRedis();
                    b.AddRedisPubSub(o => o.Enabled = pubSub);
                    b.AddRedisStreams(o =>
                    {
                        o.Enabled = !pubSub;
                        o.MaintainerEnabled = false;
                    });
                    b.AddBroadcast();
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                    o.DefaultTopic = transport;
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }

    private sealed class MissedObserver : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        private readonly List<MissedEventsReason> _reasons = [];

        public IReadOnlyCollection<MissedEventsReason> Reasons
        {
            get
            {
                lock (_reasons)
                {
                    return [.. _reasons];
                }
            }
        }

        public void OnEventsMissed(MissedEventsReason reason)
        {
            lock (_reasons)
            {
                _reasons.Add(reason);
            }
        }

        public void OnNext(ICacheEvent value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
