namespace UiPath.Caching.Broadcast.Redis;

internal interface IFetchWaiter : IDisposable
{
    /// <summary>Waits the poll interval out, or less when woken.</summary>
    Task WaitAsync(CancellationToken cancellationToken);

    /// <summary>Waits <paramref name="timeout"/> out, or less when woken.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Ends the wait in progress, or the next one.</summary>
    void Signal();
}
