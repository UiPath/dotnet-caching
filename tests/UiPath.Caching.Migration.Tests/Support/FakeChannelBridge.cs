namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>An older client's channel in memory: records what the router publishes and delivers what a test sends.</summary>
internal sealed class FakeChannelBridge : IChannelBridge
{
    private readonly List<BridgeMessage> _published = [];
    private Action<BridgeMessage>? _subscriber;

    public Exception? PublishFails { get; set; }

    /// <summary>Holds every publish until it completes.</summary>
    public TaskCompletionSource? PublishGate { get; set; }

    public bool Subscribed => _subscriber is not null;

    public IReadOnlyList<BridgeMessage> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    public async ValueTask PublishAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        lock (_published)
        {
            _published.Add(message);
        }

        if (PublishGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (PublishFails is { } failure)
        {
            throw failure;
        }
    }

    public IDisposable Subscribe(Action<BridgeMessage> onMessage)
    {
        _subscriber = onMessage;
        return new Unsubscriber(this);
    }

    public void Deliver(BridgeMessage message) => _subscriber?.Invoke(message);

    private sealed class Unsubscriber(FakeChannelBridge owner) : IDisposable
    {
        public void Dispose() => owner._subscriber = null;
    }
}
