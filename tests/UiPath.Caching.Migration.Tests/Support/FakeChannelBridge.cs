namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>An older client's channel in memory: records what the router publishes and delivers what a test sends.</summary>
internal sealed class FakeChannelBridge : IChannelBridge
{
    private readonly List<BridgeMessage> _published = [];
    private Action<BridgeMessage>? _subscriber;
    private int _cancelled;
    private int _subscribeCalls;
    private int _disposals;
    private int _entered;

    public Exception? PublishFails { get; set; }

    /// <summary>How many subscribe calls throw before one succeeds.</summary>
    public int SubscribeFailures { get; set; }

    public bool DisposeFails { get; set; }

    /// <summary>Holds Subscribe until set, as an adapter waiting on its channel does.</summary>
    public ManualResetEventSlim? SubscribeGate { get; set; }

    /// <summary>Blocks the calling thread inside PublishAsync before it returns, as an adapter waiting on a lock does.</summary>
    public ManualResetEventSlim? BlockBeforeReturn { get; set; }

    public int Cancelled => Volatile.Read(ref _cancelled);

    public int SubscribeCalls => Volatile.Read(ref _subscribeCalls);

    public int Disposals => Volatile.Read(ref _disposals);

    public bool SubscribeEntered => Volatile.Read(ref _entered) == 1;

    /// <summary>Holds every publish until it completes.</summary>
    public TaskCompletionSource? PublishGate { get; set; }

    public bool Subscribed => _subscriber is not null;

    public TaskCompletionSource SubscriptionCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        // This latch simulates a send that cannot be interrupted by cancellation.
        BlockBeforeReturn?.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
        if (PublishGate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }

        if (PublishFails is { } failure)
        {
            throw failure;
        }
    }

    public IDisposable Subscribe(Action<BridgeMessage> onMessage)
    {
        Volatile.Write(ref _entered, 1);
        SubscribeGate?.Wait();
        if (Interlocked.Increment(ref _subscribeCalls) <= SubscribeFailures)
        {
            throw new InvalidOperationException("channel unavailable");
        }

        _subscriber = onMessage;
        SubscriptionCompleted.TrySetResult();
        return new Unsubscriber(this);
    }

    public void Deliver(BridgeMessage message) => _subscriber?.Invoke(message);

    private sealed class Unsubscriber(FakeChannelBridge owner) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Increment(ref owner._disposals);
            owner._subscriber = null;
            if (owner.DisposeFails)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
