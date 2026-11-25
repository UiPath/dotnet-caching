using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

internal sealed class RedisTopicSignals(string topicName, bool streams = true)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task BarrierAsync(ITopic<ICacheEvent> publisher, ITopic<ICacheEvent> subscriber, CancellationToken token)
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
    }

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
        if (!streams && method.Name == nameof(ISubscriber.Subscribe) &&
            args is [RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags] && IncludesTopic(channel))
        {
            subscriber.Subscribe(channel, handler, flags);
            _ready.TrySetResult();
            return null;
        }

        return InterceptingProxy.Proceed;
    });

    private async ValueTask<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mux = await ConnectionMultiplexer.ConnectAsync(configuration);
        return InterceptingProxy.Wrap<IConnectionMultiplexer>(mux, (method, args) => method.Name switch
        {
            nameof(IConnectionMultiplexer.GetDatabase) => Observe(mux.GetDatabase((int)args[0]!, args[1])),
            nameof(IConnectionMultiplexer.GetSubscriber) => Observe(mux.GetSubscriber(args[0])),
            _ => InterceptingProxy.Proceed,
        });
    }

    private bool IncludesTopic(object? name) => name?.ToString()?.Contains(topicName, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<T> CreatedAsync<T>(Task<T> creating)
    {
        var result = await creating;
        _ready.TrySetResult();
        return result;
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
