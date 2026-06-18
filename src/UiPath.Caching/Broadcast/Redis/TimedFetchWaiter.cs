namespace UiPath.Caching.Broadcast.Redis;

internal sealed class TimedFetchWaiter : IFetchWaiter
{
    private readonly TimeSpan _pollInterval;

    public TimedFetchWaiter(TimeSpan pollInterval)
    {
        _pollInterval = pollInterval;
    }

    public Task WaitAsync(CancellationToken cancellationToken)
        => Task.Delay(_pollInterval, cancellationToken);

    public void Dispose() { }
}
