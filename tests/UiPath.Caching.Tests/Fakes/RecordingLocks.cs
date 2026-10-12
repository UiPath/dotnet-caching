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
