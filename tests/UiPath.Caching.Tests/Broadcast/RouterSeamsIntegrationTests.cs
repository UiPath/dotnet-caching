using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        Assert.SkipWhen(transport == KnownTopicNames.RedisStreams && !fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var publisherNode = Node("a", transport);
        var subscriberNode = Node("b", transport);
        var key = new TopicKey($"orders-{Guid.NewGuid():N}");
        var observer = new MissedObserver();
        var topic = Provider(subscriberNode, transport).Create(key);
        using var subscription = topic.Subscribe(observer);
        var ownObserver = new MissedObserver();
        var ownTopic = Provider(publisherNode, transport).Create(key);
        using var ownSubscription = ownTopic.Subscribe(ownObserver);
        (await UntilAsync(() => State(topic).IsSubscribed && State(ownTopic).IsSubscribed)).Should().BeTrue();
        var publisher = new CacheEventPublisher(
            "router",
            Provider(publisherNode, transport),
            publisherNode.GetRequiredService<ICacheEventFactory>(),
            NullLogger<CacheEventPublisher>.Instance);

        (await publisher.TopicInvalidatedAsync(key)).Should().BeTrue();

        (await UntilAsync(() => observer.Reasons.Contains(MissedEventsReason.Lost))).Should().BeTrue("the other node's public invalidation expires every subscriber of the topic");
        await Task.Delay(500, Ct);
        ownObserver.Reasons.Should().NotContain(MissedEventsReason.Lost, "a node does not hear its own events, this one included");
    }

    [Fact]
    public async Task A_pub_sub_topic_is_subscribed_once_the_server_has_the_subscription_and_not_after_it_is_disposed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", KnownTopicNames.RedisPubSub, pubSub: o => o.SubscriberDueTime = TimeSpan.FromSeconds(3));
        var topic = Provider(node, KnownTopicNames.RedisPubSub).Create(new TopicKey($"orders-{Guid.NewGuid():N}"));
        var state = State(topic);

        state.IsSubscribed.Should().BeFalse("the subscription is made on the subscriber's first timer tick, 3 s away");
        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        (await RedisTestKeys.SubscribersAsync(redis, $"{_app}:ps:{topic.TopicKey}")).Should().BeGreaterThan(0, "a subscribed topic is one the server would deliver to");

        topic.Dispose();

        state.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task A_streams_topic_is_subscribed_once_its_group_exists_and_the_reader_runs_and_not_after_it_is_disposed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var node = Node("a", KnownTopicNames.RedisStreams);
        var topic = Provider(node, KnownTopicNames.RedisStreams).Create(new TopicKey($"orders-{Guid.NewGuid():N}"));
        var state = State(topic);

        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue();
        (await GroupsAsync()).Should().NotBeEmpty("a topic reports itself subscribed only with its consumer group in place");

        topic.Dispose();

        state.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task A_pub_sub_topic_on_a_pattern_is_subscribed_once_the_server_has_the_pattern()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var pattern = RedisChannel.Pattern($"{_app}:pattern:*");
        var node = Node("a", KnownTopicNames.RedisPubSub, pubSub: o => o.RedisChannelStrategy = new FixedChannel(pattern));
        var topic = Provider(node, KnownTopicNames.RedisPubSub).Create(new TopicKey("orders"));

        (await UntilAsync(() => State(topic).IsSubscribed)).Should().BeTrue();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var channels = (RedisResult[])(await redis.GetDatabase().ExecuteAsync("PUBSUB", "CHANNELS", "*"))!;
        (await RedisTestKeys.PatternsAsync(redis)).Should().BeGreaterThan(0);
        channels.Should().NotContain(channel => ((string?)channel)!.StartsWith($"{_app}:pattern:", StringComparison.Ordinal), "the subscription is a pattern, not a channel");
    }

    [Fact]
    public async Task A_streams_topic_stops_being_subscribed_when_its_group_is_lost_and_reads_fail_and_is_again_once_they_succeed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var node = Node("a", KnownTopicNames.RedisStreams);
        var topic = Provider(node, KnownTopicNames.RedisStreams).Create(new TopicKey($"orders-{Guid.NewGuid():N}"));
        var state = State(topic);
        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = redis.GetDatabase();
        var stream = await StreamKeyAsync(redis);

        // A string where the stream was: the group is gone and cannot be made, so no read succeeds.
        await database.KeyDeleteAsync(stream);
        await database.StringSetAsync(stream, "blocked");

        (await UntilAsync(() => !state.IsSubscribed)).Should().BeTrue("the next read finds the group gone and fails");
        await Task.Delay(1500, Ct);
        state.IsSubscribed.Should().BeFalse("group creation stays blocked while the key holds a string");

        await database.KeyDeleteAsync(stream);

        (await UntilAsync(() => state.IsSubscribed)).Should().BeTrue("the reader makes the group again once it can");
        (await GroupsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_stack_on_the_primary_connection_and_the_application_each_hear_every_event_of_one_topic_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        const string Stack = "Stack";
        var application = ApplicationWithStack("app", Stack);
        var publisherNode = Node("p", KnownTopicNames.RedisStreams);
        var key = new TopicKey($"orders-{Guid.NewGuid():N}");
        var applicationObserver = new MissedObserver();
        var stackObserver = new MissedObserver();
        var applicationTopic = application.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(key);
        var stackTopic = application.GetRequiredKeyedService<ITopicFactory>(Stack).Get(KnownTopicNames.RedisStreams).Create(key);
        using var applicationSubscription = applicationTopic.Subscribe(applicationObserver);
        using var stackSubscription = stackTopic.Subscribe(stackObserver);
        (await UntilAsync(() => State(applicationTopic).IsSubscribed && State(stackTopic).IsSubscribed)).Should().BeTrue();
        var publisher = new CacheEventPublisher(
            "router",
            Provider(publisherNode, KnownTopicNames.RedisStreams),
            publisherNode.GetRequiredService<ICacheEventFactory>(),
            NullLogger<CacheEventPublisher>.Instance);

        (await publisher.TopicInvalidatedAsync(key)).Should().BeTrue();

        (await UntilAsync(() => applicationObserver.Reasons.Count > 0 && stackObserver.Reasons.Count > 0))
            .Should().BeTrue("one stream, a group each: neither splits the entries with the other");
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

    private static void Chain(ICachingBuilder b, bool pubSubEnabled, Action<RedisPubSubTopicOptions>? configurePubSub)
    {
        b.AddRedis();
        b.AddRedisPubSub(o =>
        {
            o.Enabled = pubSubEnabled;
            configurePubSub?.Invoke(o);
        });
        b.AddRedisStreams(o =>
        {
            o.Enabled = !pubSubEnabled;
            o.MaintainerEnabled = false;
        });
        b.AddBroadcast();
        b.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
        b.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
    }

    private async Task<RedisKey> StreamKeyAsync(ConnectionMultiplexer redis)
    {
        var database = redis.GetDatabase();
        foreach (var key in RedisTestKeys.Keys(redis, $"*{_app}*"))
        {
            if (await database.KeyTypeAsync(key) == RedisType.Stream)
            {
                return key;
            }
        }

        throw new InvalidOperationException("no stream was made");
    }

    private ServiceProvider ApplicationWithStack(string source, string stack)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:AppShortName"] = _app,
                ["Caching:SourceUri"] = $"urn:{source}-{_app}",
                ["Caching:DefaultTopic"] = KnownTopicNames.RedisStreams,
                ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => Chain(b.AddRedisConnection(), pubSubEnabled: false, configurePubSub: null));
        services.AddNamedCachingOnPrimaryConnection(stack, configuration, b => Chain(b, pubSubEnabled: false, configurePubSub: null));
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }

    private async Task<List<StreamGroupInfo>> GroupsAsync()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = redis.GetDatabase();
        var groups = new List<StreamGroupInfo>();
        foreach (var key in RedisTestKeys.Keys(redis, $"*{_app}*"))
        {
            if (await database.KeyTypeAsync(key) == RedisType.Stream)
            {
                groups.AddRange(await database.StreamGroupInfoAsync(key));
            }
        }

        return groups;
    }

    private ServiceProvider Node(string source, string transport, string? connectionString = null, Action<RedisPubSubTopicOptions>? pubSub = null)
    {
        var usesPubSub = transport == KnownTopicNames.RedisPubSub;
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connectionString ?? fixture.ConnectionString);
                    Chain(b, usesPubSub, pubSub);
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

    private sealed class FixedChannel(RedisChannel channel) : IRedisChannelStrategy
    {
        public RedisChannel GetRedisChannel(TopicKey topicKey) => channel;
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
