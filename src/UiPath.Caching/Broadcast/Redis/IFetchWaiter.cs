namespace UiPath.Caching.Broadcast.Redis;

internal interface IFetchWaiter : IDisposable
{
    /// <summary>Whether <see cref="ObserveRead"/> needs to know if a read found entries of other nodes.</summary>
    bool ObservesReads { get; }

    /// <summary>Waits the poll interval out, or less when woken.</summary>
    Task WaitAsync(CancellationToken cancellationToken);

    /// <summary>Waits <paramref name="timeout"/> out, or less when woken.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Ends the wait in progress, or the next one.</summary>
    void Signal();

    /// <summary>Tells the waiter whether the read after a wait found entries of other nodes, so that entries no doorbell announced shorten the waits.</summary>
    DoorbellHealth ObserveRead(bool fromOthers);
}
