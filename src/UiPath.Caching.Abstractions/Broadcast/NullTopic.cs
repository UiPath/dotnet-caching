namespace UiPath.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullTopic<T> : ITopic<T>
     where T : IEvent
{
    public static readonly ITopic<T> Instance = new NullTopic<T>();

    private NullTopic()
    {
    }

    public TopicKey TopicKey => TopicKey.Null;

    public EventHandler? OnDisposed { get; set; }

    public ValueTask<bool> PublishAsync(T @event, CancellationToken token = default) =>
        ValueTask.FromResult(true);

    public IDisposable Subscribe(IObserver<T> observer) =>
        Disposable.Empty;

    public void Dispose() => OnDisposed?.Invoke(this, EventArgs.Empty);
}
