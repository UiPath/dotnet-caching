namespace UiPath.Caching.Locking;

/// <summary>A distributed lock that says whether it took the lock at its first attempt.</summary>
internal interface IReportsLockContention
{
    /// <summary>As <see cref="IDistributedLock.AcquireAsync"/>; <c>Contended</c> is false only when the first attempt took the lock.</summary>
    ValueTask<(IAsyncDisposable Lease, bool Contended)> AcquireReportingContentionAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token);
}
