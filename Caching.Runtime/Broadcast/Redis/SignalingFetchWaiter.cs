namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class SignalingFetchWaiter(TimeSpan pollInterval) : IFetchWaiter
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);
    private volatile bool _disposed;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            await _semaphore.WaitAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with Wait — treat as a normal wake-up so the fetch loop can observe disposal.
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
