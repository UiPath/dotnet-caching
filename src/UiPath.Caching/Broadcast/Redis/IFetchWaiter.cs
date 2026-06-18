namespace UiPath.Caching.Broadcast.Redis;

internal interface IFetchWaiter : IDisposable
{
    Task WaitAsync(CancellationToken cancellationToken);
}
