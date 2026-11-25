using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

internal sealed class RedisTestSignals(Action? changed = null)
{
    private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _delivered = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ITopic<ICacheEvent>, Lazy<ObservedTopic>> _topics = new();

    public Action? TransactionQueued { get; set; }

    public void Pulse()
    {
        _changes.Writer.TryWrite(true);
        changed?.Invoke();
    }

    public Task<bool> DeliveredAsync(string key, int count, CancellationToken token) => UntilAsync(
        () => Task.FromResult(_delivered.Any(pair => pair.Key.EndsWith(key, StringComparison.Ordinal) && pair.Value >= count)), token);

    public async Task<bool> UntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await _changes.Reader.ReadAsync(token);
        }

        return true;
    }

    public IServiceCollection Observe(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ITopicProvider)).ToArray())
        {
            services.Remove(descriptor);
            services.AddSingleton(provider => Forwarding<ITopicProvider>.Wrap(
                (ITopicProvider)Resolve(provider, descriptor),
                after: (method, _, result) => method.Name == nameof(ITopicProvider.Create) && result is ITopic<ICacheEvent> topic
                    ? _topics.GetOrAdd(topic, value => new Lazy<ObservedTopic>(() => new ObservedTopic(value, this))).Value : result));
        }

        var telemetry = services.Last(descriptor => descriptor.ServiceType == typeof(ICachingTelemetryProvider));
        services.Remove(telemetry);
        services.AddSingleton(provider => Forwarding<ICachingTelemetryProvider>.Wrap(
            (ICachingTelemetryProvider)Resolve(provider, telemetry),
            after: (method, args, result) =>
            {
                if (method.Name is nameof(ICachingTelemetryProvider.TrackEvent) or nameof(ICachingTelemetryProvider.TrackMetric)
                    && args[0] is string name && (name.StartsWith("cache.peer_refresh.", StringComparison.Ordinal) || name.StartsWith("cache.rehydrate.", StringComparison.Ordinal) || (name.StartsWith(Metrics.Topic, StringComparison.Ordinal) && name.EndsWith(Metrics.Read, StringComparison.Ordinal))))
                {
                    Pulse();
                }

                return result;
            }));
        var connector = services.Last(descriptor => descriptor.ServiceType == typeof(IRedisConnector));
        services.Remove(connector);
        services.AddSingleton<IRedisConnector>(provider => new InterceptingConnector(
            (IRedisConnector)Resolve(provider, connector),
            after: (method, _, result) => method.Name switch
            {
                nameof(IDatabase.CreateTransaction) when result is ITransaction transaction => Forwarding<ITransaction>.Wrap(transaction,
                    after: (operation, _, answer) =>
                    {
                        if (operation.Name == nameof(ITransaction.ExecuteAsync))
                        {
                            TransactionQueued?.Invoke();
                        }
                        return answer;
                    }),
                nameof(IDatabase.StreamAcknowledgeAsync) when result is Task<long> acknowledged => CompletedAsync(acknowledged),
                nameof(IDatabase.LockReleaseAsync) when result is Task<bool> released => CompletedAsync(released),
                _ => result,
            }));
        return services;
    }

    private static object Resolve(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    private async Task<T> CompletedAsync<T>(Task<T> task)
    {
        var result = await task.ConfigureAwait(false);
        Pulse();
        return result;
    }

    private sealed class ObservedTopic(ITopic<ICacheEvent> inner, RedisTestSignals signals) : ITopic<ICacheEvent>, ITopicSubscriptionState
    {
        private readonly IDisposable _subscription = inner.Subscribe(new Observer(signals));

        public event Action<Exception>? Refused
        {
            add => inner.Refused += value;
            remove => inner.Refused -= value;
        }

        public TopicKey TopicKey => inner.TopicKey;

        public EventHandler? OnDisposed { get => inner.OnDisposed; set => inner.OnDisposed = value; }

        public string? Transport => inner.Transport;

        public bool IsSubscribed => inner is ITopicSubscriptionState { IsSubscribed: true };

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => inner.Subscribe(observer);

        public ValueTask<bool> PublishAsync(ICacheEvent value, CancellationToken token = default) => inner.PublishAsync(value, token);

        public void Dispose()
        {
            _subscription.Dispose();
            inner.Dispose();
        }
    }

    private sealed class Observer(RedisTestSignals signals) : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        public void OnNext(ICacheEvent value)
        {
            if ((value.Data?.Key ?? value.Key) is { } key)
            {
                signals._delivered.AddOrUpdate(key, 1, (_, count) => count + 1);
            }
            signals.Pulse();
        }

        public void OnCompleted() => signals.Pulse();

        public void OnError(Exception error) => signals.Pulse();

        public void OnEventsMissed(MissedEventsReason reason) => signals.Pulse();
    }
}
