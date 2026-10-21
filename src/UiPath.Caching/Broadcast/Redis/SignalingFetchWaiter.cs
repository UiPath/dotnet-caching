namespace UiPath.Caching.Broadcast.Redis;

internal sealed class SignalingFetchWaiter(TimeSpan pollInterval, TimeSpan? notifiedPollInterval = null) : IFetchWaiter
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);
    private volatile bool _disposed;
    private volatile bool _notifyLive;

    public Task WaitAsync(CancellationToken cancellationToken) =>
        WaitAsync(_notifyLive && notifiedPollInterval is { } notified ? notified : pollInterval, cancellationToken);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with Wait — treat as a normal wake-up so the fetch loop can observe disposal.
        }
    }

    /// <summary>While live, a wait lasts the notified interval; losing it wakes the wait in progress so the next one is the short one.</summary>
    public void SetNotifyLive(bool live)
    {
        if (_notifyLive == live)
        {
            return;
        }
        _notifyLive = live;
        if (!live)
        {
            Signal();
        }
    }

    public void Signal()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending; collapsing to one is the desired behavior.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with Signal — nothing to wake up.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _semaphore.Dispose();
    }
}
