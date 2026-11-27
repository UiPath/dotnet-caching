using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

internal sealed class RedisTopicSignals(string topicName, bool streams = true)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _groupMade;
    private volatile bool _doorbellSubscribed;
    private Func<Task>? _ring;

    /// <summary>What <see cref="Add"/> sets, for a stack that takes its options from configuration, as a router's does.</summary>
    public static IReadOnlyDictionary<string, string?> SubscribeAtOnce { get; } = new Dictionary<string, string?>
    {
        ["Caching:Broadcast:RedisStreams:NotifySubscriberDueTime"] = "00:00:00",
        ["Caching:Broadcast:RedisPubSub:SubscriberDueTime"] = "00:00:00",
    };

    /// <param name="own">The node's signals when it is both publisher and subscriber: it does not answer its own doorbell, so another's is rung for it.</param>
    public static async Task BarrierAsync(ITopic<ICacheEvent> publisher, ITopic<ICacheEvent> subscriber, CancellationToken token, RedisTopicSignals? own = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var observer = new Receipt(message => message.Id == id);
        using var subscription = subscriber.Subscribe(observer);
        var marker = new CacheEvent
        {
            Id = id,
            Source = new Uri("urn:test-topic-barrier"),
            Type = KnownEventTypes.CacheRemoved,
            Data = new CacheEventData(CacheKey.Verbatim(id)),
        };
        (await publisher.PublishAsync(marker, token)).Should().BeTrue();
        if (own?._ring is { } ring)
        {
            await ring();
        }

        await observer.Delivered.Task.WaitAsync(token);
    }

    public static async Task ObserveChangeAsync(
        ITopic<ICacheEvent> publisher,
        ITopic<ICacheEvent> subscriber,
        Func<Task> change,
        CancellationToken token,
        Predicate<ICacheEvent>? matches = null)
    {
        var observer = new Receipt(matches, listenForInvalidation: matches is null);
        using var subscription = subscriber.Subscribe(observer);
        await change();
        await observer.Delivered.Task.WaitAsync(token);
        await BarrierAsync(publisher, subscriber, token);
    }

    public static void Add(IServiceCollection services, string topicName, bool streams = true)
    {
        var signals = new RedisTopicSignals(topicName, streams);
        services.AddSingleton(signals);
        services.PostConfigure<RedisConnectionOptions>(options => options.ConnectionFactory = signals.ConnectAsync);

        // Due at once: the node's clock is stopped, and ReadyAsync runs the timers that are due without moving it.
        services.PostConfigure<RedisStreamsTopicOptions>(options => options.NotifySubscriberDueTime = TimeSpan.Zero);
        services.PostConfigure<RedisPubSubTopicOptions>(options => options.SubscriberDueTime = TimeSpan.Zero);
    }

    /// <summary>Waits until the node hears its peers on the topic. The broadcast subscribes from a timer on the node's stopped clock, which is run here.</summary>
    public static Task ReadyAsync(IServiceProvider node, CancellationToken token) =>
        ((SignalingTimeProvider)node.GetRequiredService<TimeProvider>()).RunDueTimersUntilAsync(node.GetRequiredService<RedisTopicSignals>()._ready.Task, token);

    public Task WaitAsync(CancellationToken token) => _ready.Task.WaitAsync(token);

    internal IDatabase Observe(IDatabase db) => InterceptingProxy.Wrap(db, (method, args) => method.Name switch
    {
        nameof(IDatabase.ExecuteAsync) when streams && args is ["XGROUP", object[] { Length: >= 3 } command] && IncludesTopic(command[1]) =>
            CreatedAsync(db.ExecuteAsync("XGROUP", command)),
        nameof(IDatabase.StreamCreateConsumerGroupAsync)
            when streams && args is [RedisKey key, RedisValue group, var position, bool createStream, CommandFlags flags] && IncludesTopic(key) =>
            CreatedAsync(db.StreamCreateConsumerGroupAsync(key, group, (RedisValue?)position, createStream, flags)),
        _ => InterceptingProxy.Proceed,
    });

    internal ISubscriber Observe(ISubscriber subscriber) => InterceptingProxy.Wrap(subscriber, (method, args) =>
    {
        if (streams && method.Name == nameof(ISubscriber.SubscribeAsync) &&
            args is [RedisChannel doorbell, Action<RedisChannel, RedisValue> ring, CommandFlags ringFlags] && IncludesTopic(doorbell))
        {
            _ring = () => subscriber.PublishAsync(doorbell, "urn:test-topic-barrier");
            return SubscribedAsync(subscriber.SubscribeAsync(doorbell, ring, ringFlags));
        }

        if (!streams && method.Name == nameof(ISubscriber.Subscribe) &&
            args is [RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags] && IncludesTopic(channel))
        {
            subscriber.Subscribe(channel, handler, flags);
            _ready.TrySetResult();
            return null;
        }

        return InterceptingProxy.Proceed;
    });

    // A subscribe that finds the subscription connection down is tried again a timer period later, which a stopped clock never reaches.
    private static async Task SubscriptionConnectedAsync(ConnectionMultiplexer mux, CancellationToken token)
    {
        using var restored = new SemaphoreSlim(0);
        void Restored(object? sender, ConnectionFailedEventArgs e) => restored.Release();
        mux.ConnectionRestored += Restored;
        try
        {
            // Only once commands go through: a server that is not there is the test's to report.
            var subscriber = mux.GetSubscriber();
            while (mux.IsConnected && !subscriber.IsConnected())
            {
                await restored.WaitAsync(token);
            }
        }
        finally
        {
            mux.ConnectionRestored -= Restored;
        }
    }

    private async ValueTask<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mux = await ConnectionMultiplexer.ConnectAsync(configuration);
        await SubscriptionConnectedAsync(mux, token);
        return InterceptingProxy.Wrap<IConnectionMultiplexer>(mux, (method, args) => method.Name switch
        {
            nameof(IConnectionMultiplexer.GetDatabase) => Observe(mux.GetDatabase((int)args[0]!, args[1])),
            nameof(IConnectionMultiplexer.GetSubscriber) => Observe(mux.GetSubscriber(args[0])),
            _ => InterceptingProxy.Proceed,
        });
    }

    private bool IncludesTopic(object? name) => name?.ToString()?.Contains(topicName, StringComparison.OrdinalIgnoreCase) == true;

    private async Task SubscribedAsync(Task subscribing)
    {
        await subscribing;
        _doorbellSubscribed = true;
        Listening();
    }

    private async Task<T> CreatedAsync<T>(Task<T> creating)
    {
        var result = await creating;
        _groupMade = true;
        Listening();
        return result;
    }

    // A stream is heard once its group is made and its doorbell subscribed: the reader is woken for every entry added from then on.
    private void Listening()
    {
        if (_groupMade && _doorbellSubscribed)
        {
            _ready.TrySetResult();
        }
    }

    private sealed class Receipt(Predicate<ICacheEvent>? matches, bool listenForInvalidation = false) : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnNext(ICacheEvent value)
        {
            if (matches?.Invoke(value) == true)
            {
                Delivered.TrySetResult();
            }
        }

        public void OnEventsMissed(MissedEventsReason reason)
        {
            if (listenForInvalidation && reason == MissedEventsReason.Lost)
            {
                Delivered.TrySetResult();
            }
        }

        public void OnCompleted() => Delivered.TrySetCanceled();

        public void OnError(Exception error) => Delivered.TrySetException(error);
    }
}
