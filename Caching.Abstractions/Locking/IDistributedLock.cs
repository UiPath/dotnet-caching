namespace UiPath.Platform.Caching.Locking;

public interface IDistributedLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token);
}
