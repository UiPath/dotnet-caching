using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>An in-process event bus: each node sees every event the other nodes publish, as the real topics do.</summary>
internal sealed class FakeBus
{
    private readonly List<(string Topic, Uri Node, IObserver<ICacheEvent> Observer)> _subscribers = [];

    public bool FailPublish { get; set; }

    /// <summary>What the topics report through <see cref="ITopicSubscriptionState"/>.</summary>
    public bool Subscribed { get; set; } = true;

    /// <summary>When set, a publish throws it, as a timeout does.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>When set, a publish waits for it before it goes out, as a hung broker would.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public int Published { get; private set; }

    /// <summary>Subscriptions that are in place.</summary>
    public int Subscriptions => Snapshot().Length;

    /// <summary>When set, creating a topic throws it.</summary>
    public Exception? FailCreate { get; set; }

    /// <summary>Limits <see cref="FailCreate"/> to topics whose name contains this text.</summary>
    public string FailCreateFor { get; set; } = string.Empty;

    /// <summary>Publishes that reached the broker call, whether they were held, failed or went out.</summary>
    public int Attempts { get; private set; }

    /// <summary>The next this many publishes report failure.</summary>
    public int FailNext { get; set; }

    /// <summary>Runs at the moment of every publish, after the failure check.</summary>
    public Action<ICacheEvent>? OnPublish { get; set; }

    public List<ICacheEvent> Events { get; } = [];

    public ITopicProvider For(Uri node) => new Provider(this, node);

    public void Gap(Uri node, MissedEventsReason reason)
    {
        foreach (var (_, subscriberNode, observer) in Snapshot())
        {
            if (subscriberNode == node && observer is IMissedEventsObserver missed)
            {
                missed.OnEventsMissed(reason);
            }
        }
    }

    private (string Topic, Uri Node, IObserver<ICacheEvent> Observer)[] Snapshot()
    {
        lock (_subscribers)
        {
            return [.. _subscribers];
        }
    }

    private bool Publish(string topic, Uri from, ICacheEvent ev)
    {
        if (FailPublish)
        {
            return false;
        }

        if (FailNext > 0)
        {
            FailNext--;
            return false;
        }

        Published++;
        Events.Add(ev);
        OnPublish?.Invoke(ev);
        foreach (var (subscribedTopic, node, observer) in Snapshot())
        {
            if (subscribedTopic != topic || node == from)
            {
                continue;
            }

            if (ev.Type == KnownEventTypes.TopicInvalidated && observer is IMissedEventsObserver missed)
            {
                missed.OnEventsMissed(MissedEventsReason.Lost);
            }
            else
            {
                observer.OnNext(ev);
            }
        }

        return true;
    }

    private sealed class Provider(FakeBus bus, Uri node) : ITopicProvider
    {
        public string Name => "fake";

        public bool Enabled => true;

        public ICollection<TopicKey> Keys => [];

        public ITopic<ICacheEvent> Create(TopicKey topicKey) => bus.FailCreate is { } failure && topicKey.Name.Contains(bus.FailCreateFor, StringComparison.Ordinal) ? throw failure : new Topic(bus, node, topicKey);

        public void Remove(TopicKey topicKey)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Topic(FakeBus bus, Uri node, TopicKey key) : ITopic<ICacheEvent>, ITopicSubscriptionState
    {
        public bool IsSubscribed => bus.Subscribed;

        public TopicKey TopicKey => key;

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            var entry = (key.Name, node, observer);
            lock (bus._subscribers)
            {
                bus._subscribers.Add(entry);
            }

            return new Unsubscribe(bus, entry);
        }

        public async ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default)
        {
            bus.Attempts++;
            if (bus.Hold is { } hold)
            {
                await hold.Task;
            }

            if (bus.FailWith is { } failure)
            {
                throw failure;
            }

            return bus.Publish(key.Name, node, @event);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Unsubscribe(FakeBus bus, (string Topic, Uri Node, IObserver<ICacheEvent> Observer) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._subscribers)
            {
                bus._subscribers.Remove(entry);
            }
        }
    }
}
