using Microsoft.Extensions.DependencyInjection;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>Brings a node's broadcast up on a stopped <see cref="ManualTimeProvider"/>, where its subscribe timers never fall due on their own.</summary>
internal static class FakeTimeBroadcast
{
    /// <summary>Makes the broadcast's subscribe timers due at once, so <see cref="ListenAsync"/> runs them without moving the clock.</summary>
    public static IServiceCollection SubscribeBroadcastAtOnce(this IServiceCollection services) => services
        .PostConfigure<RedisStreamsTopicOptions>(o => o.NotifySubscriberDueTime = TimeSpan.Zero)
        .PostConfigure<RedisPubSubTopicOptions>(o => o.SubscriberDueTime = TimeSpan.Zero);

    /// <inheritdoc cref="ListenAsync"/>
    public static void Listen(this ServiceProvider node, params Type[] types) =>
        Task.Run(() => node.ListenAsync(types)).GetAwaiter().GetResult();

    /// <summary>
    /// Creates the node's topic for each value type (<see cref="string"/> when none is given) and waits until it hears its peers:
    /// the Pub/Sub subscription is in place, or the stream's group exists and its doorbell is live. The clock does not move.
    /// </summary>
    public static async Task ListenAsync(this ServiceProvider node, params Type[] types)
    {
        var clock = (ManualTimeProvider)node.GetRequiredService<TimeProvider>();

        // The provider itself: the one the factory hands out may be wrapped by the test, and its topics with it.
        RedisTopicProviderBase provider = node.GetRequiredService<ITopicFactory>().Get().Name == KnownTopicNames.RedisPubSub
            ? node.GetRequiredService<RedisPubSubTopicProvider>()
            : node.GetRequiredService<RedisStreamsTopicProvider>();
        var keys = new DefaultTopicKeyStrategy();
        await node.SubscriptionConnectedAsync();
        foreach (var type in types.Length == 0 ? [typeof(string)] : types)
        {
            var topic = provider.Create(keys.GetTopicKey(type));
            if (BroadcastNodeSignals.Inner(topic) is RedisPubSubTopic<ICacheEvent> channel)
            {
                await SubscribedAsync(channel, clock);
                continue;
            }

            clock.Advance(TimeSpan.Zero);

            // The reader starts a timed wait after each read, so the state is looked at again then.
            await clock.WhenTimerAsync(() => Listening(topic));
        }
    }

    /// <summary>
    /// Waits until the node's subscription connection is up. A subscribe that finds it down is tried again a timer period later,
    /// which a stopped clock never reaches.
    /// </summary>
    public static async Task SubscriptionConnectedAsync(this ServiceProvider node)
    {
        var token = TestContext.Current.CancellationToken;
        var redis = node.GetRequiredService<IRedisConnector>();
        await redis.ConnectAsync(token);
        using var restored = new SemaphoreSlim(0);
        void Restored(object? sender, EventArgs e) => restored.Release();
        redis.OnConnectionRestored += Restored;
        try
        {
            while (!redis.Subscriber.IsConnected())
            {
                await restored.WaitAsync(token);
            }
        }
        finally
        {
            redis.OnConnectionRestored -= Restored;
        }
    }

    private static bool Listening(ITopic<ICacheEvent> topic) =>
        ((ITopicSubscriptionState)topic).IsSubscribed
        && BroadcastNodeSignals.Inner(topic) is not RedisStreamsTopic<ICacheEvent> { NotifyLive: false };

    private static async Task SubscribedAsync(RedisPubSubTopic<ICacheEvent> channel, ManualTimeProvider clock)
    {
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ready() => subscribed.TrySetResult();
        channel.Subscribed += Ready;
        try
        {
            clock.Advance(TimeSpan.Zero);
            if (!channel.IsSubscribed)
            {
                await subscribed.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            channel.Subscribed -= Ready;
        }
    }
}
