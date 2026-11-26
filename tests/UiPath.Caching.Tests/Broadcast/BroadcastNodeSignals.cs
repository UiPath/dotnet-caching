using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

internal sealed class BroadcastNodeSignals : IObserver<ICacheEvent>, IMissedEventsObserver, IDisposable
{
    private readonly ManualTimeProvider _clock;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<ITopic<ICacheEvent>> _topics = [];
    private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _marker;
    private string? _markerId;
    private bool _armed;
    private string? _key;

    private BroadcastNodeSignals(ManualTimeProvider clock) => _clock = clock;

    public static async Task<BroadcastNodeSignals> AttachAsync(ServiceProvider node, string key)
    {
        var clock = (ManualTimeProvider)node.GetRequiredService<TimeProvider>();
        var signals = new BroadcastNodeSignals(clock) { _key = key };
        var pubSub = node.GetRequiredService<RedisPubSubTopicProvider>();
        var topics = pubSub.Enabled
            ? pubSub.CreatedTopics().ToArray()
            : node.GetRequiredService<RedisStreamsTopicProvider>().CreatedTopics().ToArray();
        foreach (var topic in topics)
        {
            signals._topics.Add(topic);
            signals._subscriptions.Add(topic.Subscribe(signals));
            _ = node.GetRequiredService<IRedisConnector>().Subscriber;
            await ReadyAsync(topic, clock);

            signals._markerId = Guid.NewGuid().ToString("N");
            signals._marker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = Options.Create(new CacheOptions { SourceUri = new Uri("urn:test-signal") });
            ICacheEventFactory factory = node.GetRequiredService<ICacheEventFactory>() is CloudCacheEventFactory
                ? new CloudCacheEventFactory(options)
                : new CacheEventFactory(options);
            var marker = factory.Create("test", KnownEventTypes.CacheRemoved, new CacheEventData(signals._markerId), signals._markerId);
            (await topic.PublishAsync(marker, TestContext.Current.CancellationToken)).Should().BeTrue();
            if (!pubSub.Enabled)
            {
                await signals.TickAsync();
            }
            await signals._marker.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        signals._marker = null;
        signals._armed = true;
        var telemetry = (RecordingTelemetryProvider)node.GetRequiredService<ICachingTelemetryProvider>();
        telemetry.MetricRecorded += metric =>
        {
            if (metric.Name is "cache.peer_refresh.kept" or "cache.peer_refresh.discarded" or "cache.peer_refresh.dropped")
            {
                signals._refreshed.TrySetResult();
            }
        };
        return signals;
    }

    public static async Task ReadyAsync(ITopic<ICacheEvent> topic, ManualTimeProvider clock, TimeSpan? due = null)
    {
        if (topic is RedisPubSubTopic<ICacheEvent> channel)
        {
            var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.Subscribed += () => subscribed.TrySetResult();
            clock.Advance(due ?? TimeSpan.FromSeconds(5));
            if (!channel.IsSubscribed)
            {
                await subscribed.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        }
        else
        {
            await clock.WhenTimerAsync(() => clock.HasTimers);
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.WhenTimerAsync(() => ((ITopicSubscriptionState)topic).IsSubscribed && clock.HasTimers);
        }
    }

    public static void Read(ITopic<ICacheEvent> topic)
    {
        if (topic is StreamsFallbackTopic)
        {
            var state = typeof(StreamsFallbackTopic).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(topic)!;
            topic = (ITopic<ICacheEvent>)state.GetType().GetProperty("Inner")!.GetValue(state)!;
        }
        if (topic is RedisStreamsTopic<ICacheEvent>)
        {
            var waiter = (SignalingFetchWaiter)typeof(RedisStreamsTopic<ICacheEvent>).GetField("_waiter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(topic)!;
            waiter.Signal();
        }
    }

    public Task TickAsync()
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var topic in _topics)
        {
            Read(topic);
        }
        return Task.CompletedTask;
    }

    public async Task ReceivedAsync(object cache)
    {
        await _received.Task.WaitAsync(TestContext.Current.CancellationToken);
        var pending = cache switch
        {
            MultilayerCache value => value.RunsInFlight,
            MultilayerHashCache hash => hash.RunsInFlight,
            _ => 0,
        };
        if (pending > 0)
        {
            await _refreshed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public void OnNext(ICacheEvent value)
    {
        if (value.Id == _markerId)
        {
            _marker?.TrySetResult();
        }
        else if (_armed && value.Key == _key)
        {
            _received.TrySetResult();
        }
    }

    public void OnEventsMissed(MissedEventsReason reason)
    {
        if (_armed && reason == MissedEventsReason.Lost)
        {
            _received.TrySetResult();
        }
    }

    public void OnError(Exception error) => _received.TrySetException(error);

    public void OnCompleted() { }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }
}


