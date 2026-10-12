using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A local lock that counts what it is asked for and otherwise locks as the built-in one does.</summary>
internal sealed class RecordingLocalLock : ILocalLock, IDisposable
{
    private readonly AsyncKeyedLocalLock _inner = new(Options.Create(new CacheOptions()));
    private int _acquisitions;

    public int Acquisitions => Volatile.Read(ref _acquisitions);

    public ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token)
    {
        Interlocked.Increment(ref _acquisitions);
        return _inner.AcquireAsync(key, token);
    }

    public void Dispose() => _inner.Dispose();
}

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
