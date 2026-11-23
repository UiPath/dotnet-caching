namespace UiPath.Caching.Broadcast;

public interface ITopic<T> : IDisposable
    where T : IEvent
{
    /// <summary>
    /// Raised when the server refuses one of the topic's commands for good: an unknown command or a missing permission, never a timeout,
    /// a loading server or a client-side error. The topic still handles the refusal as before; a listener only learns of it. A topic that
    /// cannot tell keeps the default, which never raises it.
    /// </summary>
    event Action<Exception>? Refused
    {
        // A topic that never raises it has no listener to keep.
        add => _ = value;
        remove => _ = value;
    }

    TopicKey TopicKey { get; }

    EventHandler? OnDisposed { get; set; }

    /// <summary>
    /// What the topic's events travel over, for the <c>Transport</c> tag of its read metric: <c>streams</c>, <c>pubsub</c> or <c>off</c>.
    /// Null when the topic cannot say, which is the default.
    /// </summary>
    string? Transport => null;

    IDisposable Subscribe(IObserver<T> observer);

    ValueTask<bool> PublishAsync(T @event, CancellationToken token = default);
}
