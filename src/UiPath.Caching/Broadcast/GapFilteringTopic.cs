namespace UiPath.Caching.Broadcast;

/// <summary>
/// The topic a custom change token factory is handed for a cache that keeps its entries over a subscription gap: its
/// observers hear a known loss but not a gap, which that factory has no other way to learn the cache chose.
/// </summary>
internal sealed class GapFilteringTopic(ITopic<ICacheEvent> inner) : ITopic<ICacheEvent>
{
    public TopicKey TopicKey => inner.TopicKey;

    public EventHandler? OnDisposed
    {
        get => inner.OnDisposed;
        set => inner.OnDisposed = value;
    }

    public IDisposable Subscribe(IObserver<ICacheEvent> observer) => inner.Subscribe(observer switch
    {
        IKeyedObserver<ICacheEvent> and IMissedEventsObserver => new KeyedFilter(observer),
        IMissedEventsObserver => new Filter(observer),
        _ => observer,
    });

    public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => inner.PublishAsync(@event, token);

    // The topic belongs to its provider, not to this view of it.
    public void Dispose()
    {
    }

    private class Filter(IObserver<ICacheEvent> observer) : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        public void OnNext(ICacheEvent value) => observer.OnNext(value);

        public void OnError(Exception error) => observer.OnError(error);

        public void OnCompleted() => observer.OnCompleted();

        public void OnEventsMissed(MissedEventsReason reason)
        {
            if (reason != MissedEventsReason.SubscriptionGap)
            {
                ((IMissedEventsObserver)observer).OnEventsMissed(reason);
            }
        }
    }

    private sealed class KeyedFilter(IObserver<ICacheEvent> observer) : Filter(observer), IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = ((IKeyedObserver<ICacheEvent>)observer).Key;
    }
}
