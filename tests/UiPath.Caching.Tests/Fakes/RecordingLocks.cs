using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A distributed lock that counts its acquisitions and grants every one.</summary>
internal sealed class RecordingDistributedLock : IDistributedLock
{
    private int _acquisitions;

    public int Acquisitions => Volatile.Read(ref _acquisitions);

    public ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token)
    {
        Interlocked.Increment(ref _acquisitions);
        return NullDistributedLock.Instance.AcquireAsync(key, expiry, waitTimeout, token);
    }
}

/// <summary>A distributed lock that grants every acquisition and reports it as contended or not, running <see cref="OnAcquire"/> first, as a peer that held the lock would.</summary>
internal sealed class ContentionReportingDistributedLock(bool contended) : IDistributedLock, IReportsLockContention
{
    public Action? OnAcquire { get; init; }

    public ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token)
    {
        OnAcquire?.Invoke();
        return NullDistributedLock.Instance.AcquireAsync(key, expiry, waitTimeout, token);
    }

    public ValueTask<(IAsyncDisposable Lease, bool Contended)> AcquireReportingContentionAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token)
    {
        OnAcquire?.Invoke();
        return new((NoOpAsyncDisposable.Instance, contended));
    }
}
